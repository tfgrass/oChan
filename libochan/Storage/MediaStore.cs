using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using oChan.Downloader;

namespace oChan.Storage;

public sealed record MediaCommitResult(string Path, long Length, string Sha256, bool Reused);

/// <summary>
/// Commits verified media and a versioned per-thread manifest. Directory leases serialize
/// publication and manifest changes between cooperating instances and processes. Network body
/// copying happens before the lease is acquired. Programs that ignore file locks are outside
/// this coordination contract. Successfully verified files may reuse in-memory proof for at
/// most ten minutes while file timestamps, length, expectations and manifest entry are unchanged.
/// Changes preserving every fingerprint field are detected by the first restore at or after
/// that fixed deadline. Cache hits do not extend it; restarting the store discards all proof.
/// </summary>
public sealed class MediaStore
{
    private const string ManifestFileName = ".ochan-manifest.json";
    private const string DirectoryLeaseFileName = ".ochan-store.lock";
    private const int ManifestVersion = 1;
    private const int MaximumRecoveryCandidates = 256;
    private const int MaximumVerificationCacheEntries = 4096;
    private static readonly TimeSpan VerificationCacheLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Action<string, string, bool> _moveFile;
    private readonly TimeSpan _directoryLeaseTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _onFileHashStarting;
    private readonly int _verificationCacheCapacity;
    private readonly object _verificationCacheSync = new object();
    private readonly Dictionary<string, VerifiedFile> _verifiedFiles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MediaStore(TimeProvider? timeProvider = null) : this(File.Move, timeProvider: timeProvider)
    {
    }

    // The internal observer counts real disk hashing; it never substitutes verification results.
    internal MediaStore(Action<string, string, bool> moveFile, TimeSpan? directoryLeaseTimeout = null,
        TimeProvider? timeProvider = null, Action<string>? onFileHashStarting = null,
        int verificationCacheCapacity = MaximumVerificationCacheEntries)
    {
        _moveFile = moveFile ?? throw new ArgumentNullException(nameof(moveFile));
        _directoryLeaseTimeout = directoryLeaseTimeout ?? TimeSpan.FromSeconds(30);
        if (_directoryLeaseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(directoryLeaseTimeout));
        }

        if (verificationCacheCapacity < 1 || verificationCacheCapacity > MaximumVerificationCacheEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(verificationCacheCapacity));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _onFileHashStarting = onFileHashStarting;
        _verificationCacheCapacity = verificationCacheCapacity;
    }

    /// <summary>
    /// Reuses a manifest-verified file, or imports an existing file whose size and provider MD5
    /// both match. Unknown existing files are never accepted merely because they exist.
    /// </summary>
    public async Task<MediaCommitResult?> TryRestoreAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        string destination = GetDestination(item);
        byte[]? expectedMd5 = ValidateExpectedMetadata(item);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(destination)!;
        string requestedName = Path.GetFileName(destination);
        if (!HasRestoreInputs(item, destination, expectedMd5))
        {
            ForgetVerifiedFile(destination);
            return null;
        }

        SemaphoreSlim directoryLock = _directoryLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));

        await directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Opening the lease never creates the thread directory. A missing destination can
            // still be imported from its legacy path below, without a read probe creating files.
            using FileStream directoryLease = await AcquireDirectoryLeaseAsync(directory, cancellationToken).ConfigureAwait(false);
            ManifestRead manifestRead = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
            if (manifestRead.Manifest.Entries.TryGetValue(requestedName, out MediaManifestEntry? entry)
                && EntryMatchesItem(entry, item)
                && TryGetEntryPath(directory, entry!.FileName, out string? recordedPath))
            {
                FileDigest? digest = await VerifyManifestFileAsync(item, destination, recordedPath!, entry,
                    expectedMd5, cancellationToken).ConfigureAwait(false);
                if (digest != null)
                {
                    return ToResult(recordedPath!, digest, true);
                }
            }
            else
            {
                ForgetVerifiedFile(destination);
            }

            if (item.ExpectedSize.HasValue && expectedMd5 != null)
            {
                FileDigest? digest = await TryHashFileAsync(destination, cancellationToken, item.ExpectedSize.Value).ConfigureAwait(false);
                if (digest != null && MatchesExpected(item, expectedMd5, digest))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddManifestEntry(manifestRead.Manifest, item, destination, digest);
                    await SaveManifestAsync(directory, manifestRead, CancellationToken.None).ConfigureAwait(false);
                    return ToResult(destination, digest, true);
                }

                // A failed manifest commit can leave a complete file at a conflict path. Recover
                // only our bounded naming family, and require the provider's independent proof.
                foreach ((string candidatePath, string candidateSha256) in EnumerateConflictCandidates(destination))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileDigest? candidateDigest = await TryHashFileAsync(candidatePath, cancellationToken,
                        item.ExpectedSize.Value).ConfigureAwait(false);
                    if (candidateDigest == null || candidateDigest.Sha256 != candidateSha256
                        || !MatchesExpected(item, expectedMd5, candidateDigest))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    AddManifestEntry(manifestRead.Manifest, item, candidatePath, candidateDigest);
                    await SaveManifestAsync(directory, manifestRead, CancellationToken.None).ConfigureAwait(false);
                    return ToResult(candidatePath, candidateDigest, true);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // No current thread directory exists. Legacy import, if verified, will create it.
            ForgetVerifiedFile(destination);
        }
        finally
        {
            directoryLock.Release();
        }

        if (item.ExpectedSize.HasValue && expectedMd5 != null && !string.IsNullOrWhiteSpace(item.LegacyPath))
        {
            string legacyPath = Path.GetFullPath(item.LegacyPath);
            if (!PathsEqual(legacyPath, destination) && File.Exists(legacyPath))
            {
                try
                {
                    await using FileStream legacyStream = OpenRead(legacyPath);
                    MediaCommitResult imported = await CommitAsync(item, legacyStream, cancellationToken).ConfigureAwait(false);
                    return imported with { Reused = true };
                }
                catch (InvalidDataException)
                {
                    // An ambiguous or incomplete old download is preserved and needs a fresh source.
                    return null;
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Copies and verifies a complete stream before publication. After publication starts the
    /// manifest commit is deliberately not cancelled, so cancellation cannot split that step.
    /// On a manifest failure the complete file remains recoverable, but no success is returned.
    /// The source stream remains owned by the caller.
    /// </summary>
    public async Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source,
        CancellationToken cancellationToken, Func<int, CancellationToken, Task>? throttleAsync = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        string destination = GetDestination(item);
        byte[]? expectedMd5 = ValidateExpectedMetadata(item);
        string directory = Path.GetDirectoryName(destination)!;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".ochan-media-" + Guid.NewGuid().ToString("N") + ".part");

        try
        {
            FileDigest digest;
            await using (FileStream temporaryStream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                digest = await CopyAndHashAsync(source, temporaryStream, cancellationToken, throttleAsync).ConfigureAwait(false);
                if (!MatchesExpected(item, expectedMd5, digest))
                {
                    throw new InvalidDataException("Downloaded media does not match its expected size or MD5 checksum.");
                }

                await temporaryStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporaryStream.Flush(flushToDisk: true);
            }

            SemaphoreSlim directoryLock = _directoryLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
            await directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using FileStream directoryLease = await AcquireDirectoryLeaseAsync(directory, cancellationToken).ConfigureAwait(false);
                ForgetVerifiedFile(destination);
                ManifestRead manifestRead = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                string publishedPath = destination;
                bool reused = false;

                // Prefer a previously assigned conflict path when it still contains these exact bytes.
                if (manifestRead.Manifest.Entries.TryGetValue(Path.GetFileName(destination), out MediaManifestEntry? entry)
                    && EntryMatchesItem(entry, item)
                    && TryGetEntryPath(directory, entry!.FileName, out string? recordedPath)
                    && await HasSameContentAsync(recordedPath!, digest, cancellationToken).ConfigureAwait(false))
                {
                    publishedPath = recordedPath!;
                    reused = true;
                }
                else if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if (!Directory.Exists(destination)
                        && await HasSameContentAsync(destination, digest, cancellationToken).ConfigureAwait(false))
                    {
                        reused = true;
                    }
                    else
                    {
                        int conflictNumber = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            publishedPath = GetConflictPath(destination, digest.Sha256, conflictNumber);
                            if (!File.Exists(publishedPath) && !Directory.Exists(publishedPath))
                            {
                                break;
                            }

                            if (!Directory.Exists(publishedPath)
                                && await HasSameContentAsync(publishedPath, digest, cancellationToken).ConfigureAwait(false))
                            {
                                reused = true;
                                break;
                            }

                            conflictNumber = checked(conflictNumber + 1);
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!reused)
                {
                    // No overwrite: a competing or foreign destination is never truncated.
                    _moveFile(temporaryPath, publishedPath, false);
                }

                AddManifestEntry(manifestRead.Manifest, item, publishedPath, digest);
                await SaveManifestAsync(directory, manifestRead, CancellationToken.None).ConfigureAwait(false);
                return ToResult(publishedPath, digest, reused);
            }
            finally
            {
                directoryLock.Release();
            }
        }
        finally
        {
            DeleteOwnTemporaryFile(temporaryPath);
        }
    }

    private static string GetDestination(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string destination = Path.GetFullPath(item.DestinationPath);
        string name = Path.GetFileName(destination);
        if (!IsSafeFileName(name) || name.StartsWith(".ochan-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The media destination must be a portable file name outside the store's metadata namespace.", nameof(item));
        }

        return destination;
    }

    private static bool HasRestoreInputs(DownloadItem item, string destination, byte[]? expectedMd5)
    {
        string manifestPath = Path.Combine(Path.GetDirectoryName(destination)!, ManifestFileName);
        if (File.Exists(manifestPath) || Directory.Exists(manifestPath))
        {
            return true;
        }

        if (!item.ExpectedSize.HasValue || expectedMd5 == null)
        {
            return false;
        }

        if (File.Exists(destination) || Directory.Exists(destination) || File.Exists(item.LegacyPath))
        {
            return true;
        }

        try
        {
            foreach ((string _, string _) in EnumerateConflictCandidates(destination))
            {
                return true;
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        // A concurrent publication missed by this cheap probe only causes a redundant source
        // read: CommitAsync always rereads state under the lease before publishing anything.
        return false;
    }

    private static byte[]? ValidateExpectedMetadata(DownloadItem item)
    {
        if (item.ExpectedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(item), "An expected media size cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(item.ExpectedMd5))
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(item.ExpectedMd5);
            if (bytes.Length == 16)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("ExpectedMd5 must be a base64-encoded 16-byte MD5 checksum.", nameof(item));
    }

    private static bool MatchesExpected(DownloadItem item, byte[]? expectedMd5, FileDigest digest)
    {
        return (!item.ExpectedSize.HasValue || item.ExpectedSize.Value == digest.Length)
            && (expectedMd5 == null || CryptographicOperations.FixedTimeEquals(expectedMd5, digest.Md5));
    }

    private static async Task<FileDigest> CopyAndHashAsync(Stream source, Stream? destination,
        CancellationToken cancellationToken, Func<int, CancellationToken, Task>? throttleAsync = null)
    {
        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = new byte[81920];
        long length = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (throttleAsync != null)
            {
                await throttleAsync(bytesRead, cancellationToken).ConfigureAwait(false);
            }

            if (destination != null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }

            sha256.AppendData(buffer, 0, bytesRead);
            md5.AppendData(buffer, 0, bytesRead);
            length = checked(length + bytesRead);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new FileDigest(length, Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(), md5.GetHashAndReset());
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task<FileDigest?> TryHashFileAsync(string path, CancellationToken cancellationToken,
        long? expectedLength = null)
    {
        if (Directory.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream = OpenRead(path);
            if (expectedLength.HasValue && stream.Length != expectedLength.Value)
            {
                return null;
            }

            _onFileHashStarting?.Invoke(path);
            return await CopyAndHashAsync(stream, null, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<bool> HasSameContentAsync(string path, FileDigest expected, CancellationToken cancellationToken)
    {
        FileDigest? actual = await TryHashFileAsync(path, cancellationToken, expected.Length).ConfigureAwait(false);
        return actual != null && actual.Length == expected.Length && actual.Sha256 == expected.Sha256;
    }

    private async Task<FileDigest?> VerifyManifestFileAsync(DownloadItem item, string cacheKey, string actualPath,
        MediaManifestEntry entry, byte[]? expectedMd5, CancellationToken cancellationToken)
    {
        FileStamp? before = ReadFileStamp(actualPath);
        if (before == null || before.Length != entry.Length
            || (item.ExpectedSize.HasValue && before.Length != item.ExpectedSize.Value))
        {
            ForgetVerifiedFile(cacheKey);
            return null;
        }

        VerificationFingerprint fingerprint = new VerificationFingerprint(actualPath, before,
            item.ExpectedSize, expectedMd5 == null ? null : Convert.ToBase64String(expectedMd5),
            new ManifestEntryFingerprint(entry.MediaIdentifier, entry.SourceUrl, entry.FileName, entry.Length,
                entry.Sha256, entry.Md5, entry.ExpectedMd5, entry.OriginalFileName));
        FileDigest? cached = FindVerifiedFile(cacheKey, fingerprint);
        if (cached != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        FileDigest? digest = await TryHashFileAsync(actualPath, cancellationToken, entry.Length).ConfigureAwait(false);
        if (digest == null || !string.Equals(digest.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
            || !MatchesExpected(item, expectedMd5, digest))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileStamp? after = ReadFileStamp(actualPath);
        if (before != after)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        RememberVerifiedFile(cacheKey, fingerprint, digest);
        return digest;
    }

    private static FileStamp? ReadFileStamp(string path)
    {
        try
        {
            FileInfo file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0)
            {
                return null;
            }

            return new FileStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private FileDigest? FindVerifiedFile(string key, VerificationFingerprint fingerprint)
    {
        lock (_verificationCacheSync)
        {
            if (_verifiedFiles.TryGetValue(key, out VerifiedFile? verified))
            {
                TimeSpan age = _timeProvider.GetElapsedTime(verified.VerifiedAtTimestamp);
                if (verified.Fingerprint == fingerprint && age >= TimeSpan.Zero && age < VerificationCacheLifetime)
                {
                    return verified.Digest;
                }
            }

            _verifiedFiles.Remove(key);
            return null;
        }
    }

    private void RememberVerifiedFile(string key, VerificationFingerprint fingerprint, FileDigest digest)
    {
        lock (_verificationCacheSync)
        {
            if (!_verifiedFiles.ContainsKey(key) && _verifiedFiles.Count >= _verificationCacheCapacity)
            {
                string? evicted = null;
                foreach (string existing in _verifiedFiles.Keys)
                {
                    evicted = existing;
                    break;
                }

                if (evicted != null)
                {
                    _verifiedFiles.Remove(evicted);
                }
            }

            _verifiedFiles[key] = new VerifiedFile(fingerprint, digest, _timeProvider.GetTimestamp());
        }
    }

    private void ForgetVerifiedFile(string key)
    {
        lock (_verificationCacheSync)
        {
            _verifiedFiles.Remove(key);
        }
    }

    private static bool EntryMatchesItem(MediaManifestEntry? entry, DownloadItem item)
    {
        return entry != null && entry.MediaIdentifier == item.MediaIdentifier && entry.Length >= 0
            && entry.Sha256 != null && entry.Sha256.Length == 64;
    }

    private static bool TryGetEntryPath(string directory, string? fileName, out string? path)
    {
        path = null;
        if (!IsSafeFileName(fileName) || fileName!.StartsWith(".ochan-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = Path.Combine(directory, fileName);
        return true;
    }

    private static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".."
            || fileName.EndsWith('.') || fileName.EndsWith(' ') || fileName.Length > 255)
        {
            return false;
        }

        foreach (char character in fileName)
        {
            if (char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        string stem = fileName.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9');
    }

    private static string GetConflictPath(string destination, string sha256, int conflictNumber)
    {
        string disambiguator = conflictNumber == 0 ? string.Empty : "." + conflictNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(Path.GetDirectoryName(destination)!,
            Path.GetFileNameWithoutExtension(destination) + "." + sha256 + disambiguator + Path.GetExtension(destination));
    }

    private static IEnumerable<(string Path, string Sha256)> EnumerateConflictCandidates(string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        string prefix = Path.GetFileNameWithoutExtension(destination) + ".";
        string extension = Path.GetExtension(destination);
        int inspected = 0;
        foreach (string path in Directory.EnumerateFiles(directory, prefix + "*" + extension, SearchOption.TopDirectoryOnly))
        {
            if (++inspected > MaximumRecoveryCandidates)
            {
                yield break;
            }

            string name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(extension, StringComparison.Ordinal)
                || name.Length < prefix.Length + 64 + extension.Length)
            {
                continue;
            }

            string suffix = name.Substring(prefix.Length, name.Length - prefix.Length - extension.Length);
            string sha256 = suffix[..64];
            bool validSha256 = true;
            foreach (char character in sha256)
            {
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))
                {
                    validSha256 = false;
                    break;
                }
            }

            if (!validSha256)
            {
                continue;
            }

            if (suffix.Length > 64 && (suffix.Length < 66 || suffix[64] != '.' || suffix[65] == '0'
                || !int.TryParse(suffix[65..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int conflictNumber) || conflictNumber <= 0))
            {
                continue;
            }

            yield return (path, sha256);
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static MediaCommitResult ToResult(string path, FileDigest digest, bool reused)
    {
        return new MediaCommitResult(path, digest.Length, digest.Sha256, reused);
    }

    private async Task<FileStream> AcquireDirectoryLeaseAsync(string directory, CancellationToken cancellationToken)
    {
        string lockPath = Path.Combine(directory, DirectoryLeaseFileName);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the empty lock file after disposing this handle. Deleting it can let a
                // second process lock a different inode while the first lease is still active.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                TimeSpan remaining = _directoryLeaseTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new IOException($"Timed out waiting for the media directory lease at '{directory}'.", exception);
                }

                TimeSpan retryDelay = remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        // FileStream reports Windows ERROR_SHARING_VIOLATION as HRESULT 0x80070020.
        // Unix FileShare conflicts use flock's EWOULDBLOCK errno (Linux 11, macOS 35).
        // Other IO errors must propagate immediately rather than masquerading as contention.
        return exception.HResult == unchecked((int)0x80070020)
            || (OperatingSystem.IsLinux() && exception.HResult == 11)
            || (OperatingSystem.IsMacOS() && exception.HResult == 35);
    }

    private static void AddManifestEntry(MediaManifest manifest, DownloadItem item, string actualPath, FileDigest digest)
    {
        manifest.Entries[Path.GetFileName(item.DestinationPath)] = new MediaManifestEntry
        {
            MediaIdentifier = item.MediaIdentifier,
            SourceUrl = item.DownloadUri.AbsoluteUri,
            FileName = Path.GetFileName(actualPath),
            Length = digest.Length,
            Sha256 = digest.Sha256,
            Md5 = Convert.ToBase64String(digest.Md5),
            ExpectedMd5 = item.ExpectedMd5,
            OriginalFileName = item.OriginalFileName
        };
    }

    private static async Task<ManifestRead> ReadManifestAsync(string directory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, ManifestFileName);
        try
        {
            await using FileStream stream = OpenRead(path);
            MediaManifest? manifest = await JsonSerializer.DeserializeAsync<MediaManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (manifest != null && manifest.Version > 0 && manifest.Version != ManifestVersion)
            {
                throw new InvalidDataException("This media manifest was written with an unsupported schema version.");
            }

            if (manifest == null || manifest.Version < 1 || manifest.Entries == null)
            {
                return NewManifestRead(corrupt: true);
            }

            return new ManifestRead(manifest, false);
        }
        catch (JsonException)
        {
            return NewManifestRead(corrupt: true);
        }
        catch (FileNotFoundException)
        {
            return NewManifestRead(corrupt: false);
        }
        catch (DirectoryNotFoundException)
        {
            return NewManifestRead(corrupt: false);
        }
    }

    private static ManifestRead NewManifestRead(bool corrupt)
    {
        return new ManifestRead(new MediaManifest { Version = ManifestVersion }, corrupt);
    }

    private async Task SaveManifestAsync(string directory, ManifestRead manifestRead, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, ManifestFileName);
        string temporaryPath = Path.Combine(directory, ".ochan-manifest-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, manifestRead.Manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (manifestRead.Corrupt)
            {
                // Preserve the unreadable input before replacing it with a reconstructable manifest.
                File.Copy(destination, destination + ".corrupt-" + Guid.NewGuid().ToString("N"), false);
            }

            _moveFile(temporaryPath, destination, true);
        }
        finally
        {
            DeleteOwnTemporaryFile(temporaryPath);
        }
    }

    private static void DeleteOwnTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // An unremovable .part is never a completion marker. Do not mask the original failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record FileDigest(long Length, string Sha256, byte[] Md5);
    private sealed record FileStamp(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
    private sealed record ManifestEntryFingerprint(string MediaIdentifier, string SourceUrl, string FileName,
        long Length, string Sha256, string Md5, string? ExpectedMd5, string? OriginalFileName);
    private sealed record VerificationFingerprint(string ActualPath, FileStamp Stamp, long? ExpectedSize,
        string? ExpectedMd5, ManifestEntryFingerprint ManifestEntry);
    private sealed record VerifiedFile(VerificationFingerprint Fingerprint, FileDigest Digest, long VerifiedAtTimestamp);
    private sealed record ManifestRead(MediaManifest Manifest, bool Corrupt);

    private sealed class MediaManifest
    {
        public MediaManifest() { }
        public int Version { get; set; }
        public Dictionary<string, MediaManifestEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class MediaManifestEntry
    {
        public MediaManifestEntry() { }
        public string MediaIdentifier { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public string? ExpectedMd5 { get; set; }
        public string? OriginalFileName { get; set; }
    }
}

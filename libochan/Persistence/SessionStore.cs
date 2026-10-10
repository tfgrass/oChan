using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace oChan.Persistence;

public sealed class SessionSnapshot
{
    // Setters let source-generated JSON loading preserve defaults for omitted legacy
    // properties. The store takes defensive copies at its public boundary.
    public int SchemaVersion { get; set; } = 1;
    public string[] ThreadUrls { get; set; } = Array.Empty<string>();
    public string[] BoardUrls { get; set; } = Array.Empty<string>();
    public string[] SuppressedThreadUrls { get; set; } = Array.Empty<string>();

    /// <summary>Maps a canonical thread or board URL to its assigned absolute download root.</summary>
    public Dictionary<string, string> DownloadDirectories { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <param name="Snapshot">The loaded session, a valid backup, or an empty session when no usable snapshot exists.</param>
/// <param name="Warning">Recovery details to show to the user, or null when loading succeeded normally.</param>
/// <param name="Recovered">True when a missing or invalid primary required an existing backup or defaults.</param>
public sealed record SessionLoadResult(SessionSnapshot Snapshot, string? Warning, bool Recovered);

public interface ISessionStore
{
    Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Loads and atomically stores session snapshots while an explicit profile lease is held.</summary>
public sealed class SessionStore : ISessionStore
{
    private readonly string _filePath;
    private readonly ProfileLease _lease;

    public SessionStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        string directory = ProfileLease.NormalizeDirectory(profileDirectory);
        lease.EnsureMatches(directory);
        _filePath = Path.Combine(directory, "saved_urls.json");
        _lease = lease;
    }

    public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _lease.RunExclusive(() => LoadCore(cancellationToken)), cancellationToken);
    }

    public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture the caller's mutable collections before scheduling any asynchronous work.
        SessionSnapshot captured = ValidateAndCopy(snapshot);
        return Task.Run(() => _lease.RunExclusive(() =>
        {
            SaveCore(captured, cancellationToken);
            return true;
        }), cancellationToken);
    }

    private SessionLoadResult LoadCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadStatus status = TryRead(_filePath, out SessionSnapshot? snapshot, out string? error);
        if (status == LoadStatus.Valid)
            return new SessionLoadResult(snapshot!, null, false);

        cancellationToken.ThrowIfCancellationRequested();
        string backupPath = _filePath + ".bak";
        LoadStatus backupStatus = TryRead(backupPath, out SessionSnapshot? backup, out string? backupError);
        if (backupStatus == LoadStatus.Valid)
        {
            string warning = $"The session could not be loaded from '{_filePath}' ({error ?? "file missing"}). " +
                $"Recovered the session from backup '{backupPath}'. The original files were preserved.";
            return new SessionLoadResult(backup!, warning, true);
        }

        if (status == LoadStatus.Missing && backupStatus == LoadStatus.Missing)
            return new SessionLoadResult(new SessionSnapshot(), null, false);

        string defaultWarning = $"The session could not be loaded from '{_filePath}' ({error ?? "file missing"}); " +
            $"no valid backup was available ({backupError ?? "file missing"}). " +
            "An empty session is in use. The original files were preserved.";
        return new SessionLoadResult(new SessionSnapshot(), defaultWarning, true);
    }

    private void SaveCore(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.SessionSnapshot);
        LoadStatus currentStatus = TryRead(_filePath, out _, out _);
        string backupPath = _filePath + ".bak";
        LoadStatus backupStatus = TryRead(backupPath, out _, out _);
        cancellationToken.ThrowIfCancellationRequested();
        if (currentStatus == LoadStatus.Valid && backupStatus == LoadStatus.Invalid)
            AtomicJsonFile.PreserveFile(backupPath);

        AtomicJsonFile.WriteAllText(_filePath, json, currentStatus == LoadStatus.Valid, cancellationToken);
    }

    private static SessionSnapshot ValidateAndCopy(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported session schema version: {snapshot.SchemaVersion}.");
        if (snapshot.ThreadUrls == null || snapshot.BoardUrls == null || snapshot.SuppressedThreadUrls == null || snapshot.DownloadDirectories == null)
            throw new InvalidDataException("Session URL collections and download directories must not be null.");

        string[] threads = CopyUrls(snapshot.ThreadUrls, nameof(snapshot.ThreadUrls));
        string[] boards = CopyUrls(snapshot.BoardUrls, nameof(snapshot.BoardUrls));
        string[] suppressed = CopyUrls(snapshot.SuppressedThreadUrls, nameof(snapshot.SuppressedThreadUrls));
        Dictionary<string, string> directories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in snapshot.DownloadDirectories)
        {
            ValidateUrl(entry.Key, nameof(snapshot.DownloadDirectories));
            if (string.IsNullOrWhiteSpace(entry.Value) || !Path.IsPathFullyQualified(entry.Value))
                throw new InvalidDataException("Every assigned download directory must be an absolute path.");
            directories.Add(entry.Key, Path.GetFullPath(entry.Value));
        }

        return new SessionSnapshot
        {
            SchemaVersion = snapshot.SchemaVersion,
            ThreadUrls = threads,
            BoardUrls = boards,
            SuppressedThreadUrls = suppressed,
            DownloadDirectories = directories
        };
    }

    private static string[] CopyUrls(string[] urls, string collectionName)
    {
        string[] copied = urls.ToArray();
        foreach (string url in copied)
            ValidateUrl(url, collectionName);
        return copied;
    }

    private static void ValidateUrl(string url, string collectionName)
    {
        if (string.IsNullOrWhiteSpace(url) || url != url.Trim() ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"{collectionName} contains an invalid absolute HTTP(S) URL.");
        }
    }

    private static LoadStatus TryRead(string filePath, out SessionSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (FileNotFoundException)
        {
            return LoadStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return LoadStatus.Missing;
        }
        // Only missing-file read errors permit recovery. Locks, permissions, and
        // other read failures must reach the caller before load or save continues.
        try
        {
            SessionSnapshot? parsed = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionSnapshot);
            if (parsed == null)
                throw new InvalidDataException("The session document contains null.");
            snapshot = ValidateAndCopy(parsed);
            return LoadStatus.Valid;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return LoadStatus.Invalid;
        }
    }

    private enum LoadStatus { Missing, Valid, Invalid }
}

[JsonSerializable(typeof(SessionSnapshot))]
internal partial class SessionJsonContext : JsonSerializerContext
{
}

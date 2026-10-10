using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Storage;

namespace oChan.Tests;

public sealed class MediaStoreTests
{
    private static readonly byte[] Payload = [1, 2, 3, 5, 8, 13, 21, 34];
    private const string ProbeRootVariable = "OCHAN_MEDIASTORE_LEASE_PROBE_ROOT";
    private const string ProbeAvailableVariable = "OCHAN_MEDIASTORE_LEASE_PROBE_AVAILABLE";

    [Fact]
    public void StorageLayoutSeparatesSourcesAndMediaRegardlessOfOriginalNames()
    {
        using TempDirectory directory = new TempDirectory();

        string first = StorageLayout.GetMediaPath(directory.Path, "4chan", "g", "123", "456", ".jpg");
        string second = StorageLayout.GetMediaPath(directory.Path, "4chan", "g", "123", "789", ".jpg");
        string otherSource = StorageLayout.GetMediaPath(directory.Path, "8kun", "g", "123", "456", ".jpg");

        Assert.Equal(Path.Combine(directory.Path, "4chan", "g", "123", "456.jpg"), first);
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, otherSource);
        Assert.True(Path.IsPathFullyQualified(first));
        Assert.Equal(Path.GetDirectoryName(first), StorageLayout.GetThreadDirectory(directory.Path, "4chan", "g", "123"));
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(first)), StorageLayout.GetBoardDirectory(directory.Path, "4chan", "g"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("source/child")]
    [InlineData("source\\child")]
    [InlineData("CON")]
    [InlineData("LPT1")]
    [InlineData("source.")]
    public void StorageLayoutRejectsAmbiguousOrNonportableSegments(string invalidSource)
    {
        using TempDirectory directory = new TempDirectory();

        Assert.Throws<ArgumentException>(() =>
            StorageLayout.GetMediaPath(directory.Path, invalidSource, "g", "123", "456", ".jpg"));
    }

    [Fact]
    public void StorageLayoutResolvesTheRootBeforeAssigningAFile()
    {
        using TempDirectory directory = new TempDirectory();
        string relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), directory.Path);

        string actual = StorageLayout.GetMediaPath(relativeRoot, "4chan", "g", "123", "456", ".JPG");

        Assert.Equal(Path.Combine(directory.Path, "4chan", "g", "123", "456.jpg"), actual);
    }

    [Fact]
    public async Task PartialReadFailureLeavesNoPublishedOrTemporaryFileAndRetrySucceeds()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory);
        MediaStore store = new MediaStore();
        using PartialFailureStream broken = new PartialFailureStream(Payload);

        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(item, broken, CancellationToken.None));

        Assert.False(File.Exists(item.DestinationPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));

        MediaCommitResult committed = await CommitBytesAsync(store, item, Payload);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(committed.Path));
        Assert.Equal(Payload.Length, committed.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Payload)), committed.Sha256, ignoreCase: true);
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterSomeBytesLeavesNoCompletionProofAndCanBeRetried()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        DownloadItem item = CreateItem(board, directory);
        MediaStore store = new MediaStore();
        using PartialFailureStream broken = new PartialFailureStream(Payload, cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(item, broken, cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        MediaCommitResult committed = await CommitBytesAsync(store, item, Payload);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(committed.Path));
    }

    [Fact]
    public async Task CancellationAfterCompleteBodyButBeforePublicationLeavesNoCompletionProof()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        using CancelAtEndStream source = new CancelAtEndStream(Payload, cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MediaStore().CommitAsync(item, source, cancellation.Token));

        Assert.Equal(Payload.Length, source.Position);
        Assert.False(File.Exists(item.DestinationPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterMediaPublicationStillCompletesTheManifest(bool cancelAtManifestPublication)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        MediaStore store = new MediaStore((source, destination, overwrite) =>
        {
            bool publishingManifest = IsManifest(destination);
            if (publishingManifest && cancelAtManifestPublication)
            {
                cancellation.Cancel();
            }

            File.Move(source, destination, overwrite);
            if (!publishingManifest && !cancelAtManifestPublication)
            {
                cancellation.Cancel();
            }
        });
        using MemoryStream content = new MemoryStream(Payload, writable: false);

        MediaCommitResult committed = await store.CommitAsync(item, content, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(item.DestinationPath, committed.Path);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(committed.Path));
        // There is no provider hash to repair a missing manifest: restoration proves its commit.
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(committed.Path, restored.Path);
        Assert.True(restored.Reused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncorrectProviderSizeOrHashRejectsTheDownload(bool incorrectSize)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem valid = CreateItem(board, directory);
        DownloadItem invalid = new DownloadItem(valid.DownloadUri, valid.DestinationPath, board, valid.Thread,
            valid.MediaIdentifier, expectedSize: incorrectSize ? Payload.Length + 1 : Payload.Length,
            expectedMd5: incorrectSize ? valid.ExpectedMd5 : Convert.ToBase64String(MD5.HashData(new byte[] { 99 })));
        MediaStore store = new MediaStore();

        await Assert.ThrowsAsync<InvalidDataException>(() => CommitBytesAsync(store, invalid, Payload));

        Assert.False(File.Exists(valid.DestinationPath));
        Assert.Null(await store.TryRestoreAsync(valid, CancellationToken.None));
        MediaCommitResult committed = await CommitBytesAsync(store, valid, Payload);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(committed.Path));
    }

    [Fact]
    public async Task ConcurrentCompletionsRetainEveryManifestEntryAcrossRestart()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaStore store = new MediaStore();
        DownloadItem[] items = Enumerable.Range(1, 32)
            .Select(index => CreateItem(board, directory, index.ToString(), provideHash: false)).ToArray();

        MediaCommitResult[] results = await Task.WhenAll(items.Select(item => CommitBytesAsync(store, item, Payload)))
            .WaitAsync(TestWait.Timeout);

        Assert.Equal(items.Length, results.Select(result => result.Path).Distinct().Count());
        MediaStore restarted = new MediaStore();
        foreach (DownloadItem item in items)
        {
            MediaCommitResult? restored = await restarted.TryRestoreAsync(item, CancellationToken.None);
            Assert.NotNull(restored);
            Assert.True(restored.Reused);
            Assert.Equal(item.DestinationPath, restored.Path);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(restored.Path));
        }
    }

    [Fact]
    public async Task DifferentStoreInstancesSerializePublicationAndRetainBothManifestEntries()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        using ManualResetEventSlim finishFirstPublication = new ManualResetEventSlim();
        TaskCompletionSource firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadItem firstItem = CreateItem(board, directory, "456", provideHash: false);
        DownloadItem secondItem = CreateItem(board, directory, "789", provideHash: false);
        MediaStore firstStore = new MediaStore((source, destination, overwrite) =>
        {
            File.Move(source, destination, overwrite);
            if (!IsManifest(destination))
            {
                firstPublished.TrySetResult();
                if (!finishFirstPublication.Wait(TestWait.Timeout))
                {
                    throw new TimeoutException("The publication test gate was not released.");
                }
            }
        });
        Task<MediaCommitResult> first = Task.Run(() => CommitBytesAsync(firstStore, firstItem, Payload));

        try
        {
            await firstPublished.Task.WaitAsync(TestWait.Timeout);
            Task<MediaCommitResult> second = CommitBytesAsync(new MediaStore(), secondItem, Payload);
            await Task.Delay(100);
            Assert.False(second.IsCompleted);
            finishFirstPublication.Set();
            await Task.WhenAll(first, second).WaitAsync(TestWait.Timeout);
        }
        finally
        {
            finishFirstPublication.Set();
            await first.WaitAsync(TestWait.Timeout);
        }

        MediaStore restarted = new MediaStore();
        Assert.NotNull(await restarted.TryRestoreAsync(firstItem, CancellationToken.None));
        Assert.NotNull(await restarted.TryRestoreAsync(secondItem, CancellationToken.None));
    }

    [Fact]
    public async Task DirectoryLeaseContentionHasAFiniteAcquisitionDeadline()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        using FileStream heldLease = new FileStream(Path.Combine(threadDirectory, ".ochan-store.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        TimeSpan waitLimit = TimeSpan.FromMilliseconds(150);
        MediaStore store = new MediaStore(File.Move, waitLimit);
        Stopwatch elapsed = Stopwatch.StartNew();

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => CommitBytesAsync(store, item, Payload).WaitAsync(TestWait.Timeout));

        Assert.True(elapsed.Elapsed >= waitLimit);
        Assert.Contains("Timed out waiting", failure.Message);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.False(File.Exists(item.DestinationPath));
        Assert.Empty(Directory.GetFiles(threadDirectory, "*.part"));
    }

    [Fact]
    public async Task DirectoryLeaseContentionCanBeCancelledBeforePublication()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        DownloadItem item = CreateItem(board, directory);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        using FileStream heldLease = new FileStream(Path.Combine(threadDirectory, ".ochan-store.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using MemoryStream source = new MemoryStream(Payload, writable: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MediaStore().CommitAsync(item, source, cancellation.Token).WaitAsync(TestWait.Timeout));

        Assert.False(File.Exists(item.DestinationPath));
        Assert.Empty(Directory.GetFiles(threadDirectory, "*.part"));
    }

    [Fact]
    public async Task NonSharingLeaseErrorsAreNotRetriedAsContention()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(Path.Combine(threadDirectory, ".ochan-store.lock"));

        Exception? failure = await Record.ExceptionAsync(
            () => CommitBytesAsync(new MediaStore(), item, Payload).WaitAsync(TestWait.Timeout));

        Assert.NotNull(failure);
        Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
        Assert.DoesNotContain("Timed out waiting", failure.Message);
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task RestoreWithoutExistingMediaDoesNotCreateDirectoriesOrLockFiles()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory);
        MediaStore store = new MediaStore();

        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));

        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(threadDirectory));
    }

    [Fact]
    public async Task DirectoryLeaseCoordinatesProcessesAndPreservesEarlierManifestEntries()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem firstItem = CreateItem(board, directory, "456", provideHash: false);
        await CommitBytesAsync(new MediaStore(), firstItem, Payload);
        string lockPath = Path.Combine(Path.GetDirectoryName(firstItem.DestinationPath)!, ".ochan-store.lock");
        using (FileStream heldLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await RunStoreLeaseProbeAsync(directory.Path, expectedAvailable: false);
        }

        Assert.True(File.Exists(lockPath));
        await RunStoreLeaseProbeAsync(directory.Path, expectedAvailable: true);

        MediaStore restarted = new MediaStore();
        Assert.NotNull(await restarted.TryRestoreAsync(firstItem, CancellationToken.None));
        Assert.NotNull(await restarted.TryRestoreAsync(CreateItem(board, directory, "789", provideHash: false), CancellationToken.None));
    }

    [Fact]
    public async Task MediaStoreLeaseChildProbe()
    {
        string? root = Environment.GetEnvironmentVariable(ProbeRootVariable);
        if (root == null)
        {
            return;
        }

        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, root, "789", provideHash: false);
        MediaStore store = new MediaStore(File.Move, TimeSpan.FromMilliseconds(150));
        if (Environment.GetEnvironmentVariable(ProbeAvailableVariable) == "true")
        {
            await CommitBytesAsync(store, item, Payload);
            Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => CommitBytesAsync(store, item, Payload));
            Assert.False(File.Exists(item.DestinationPath));
            // A lease in one thread does not exclude an independent archive directory.
            DownloadItem independent = CreateItem(board, Path.Combine(root, "independent"), "789", provideHash: false);
            await CommitBytesAsync(store, independent, Payload);
        }
    }

    private static async Task RunStoreLeaseProbeAsync(string root, bool expectedAvailable)
    {
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(MediaStoreTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=oChan.Tests.MediaStoreTests.MediaStoreLeaseChildProbe");
        start.Environment[ProbeRootVariable] = root;
        start.Environment[ProbeAvailableVariable] = expectedAvailable ? "true" : "false";
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the media store lease subprocess.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync();
            throw new TimeoutException($"The media store lease subprocess timed out. {await output} {await errors}");
        }

        Assert.True(child.ExitCode == 0, $"Media store lease subprocess failed. {await output} {await errors}");
    }

    [Fact]
    public async Task RefreshedMediaUrlPreservesTheSameManifestBackedIdentity()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem original = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), original, Payload);
        DownloadItem refreshed = new DownloadItem(new Uri("https://new-cdn.stub.test/media/456.bin?token=fresh"),
            original.DestinationPath, board, original.Thread, original.MediaIdentifier,
            expectedSize: original.ExpectedSize, originalFileName: original.OriginalFileName);

        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(refreshed, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.True(restored.Reused);
        Assert.Equal(original.DestinationPath, restored.Path);
    }

    [Fact]
    public async Task NewProviderChecksumCannotBeOverriddenByAnOlderManifest()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem original = CreateItem(board, directory);
        await CommitBytesAsync(new MediaStore(), original, Payload);
        DownloadItem changed = new DownloadItem(original.DownloadUri, original.DestinationPath, board,
            original.Thread, original.MediaIdentifier, expectedSize: original.ExpectedSize,
            expectedMd5: Convert.ToBase64String(MD5.HashData(Payload.Reverse().ToArray())));

        Assert.Null(await new MediaStore().TryRestoreAsync(changed, CancellationToken.None));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(original.DestinationPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrChangedFileInvalidatesItsManifestProof(bool removeFile)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        MediaStore store = new MediaStore();
        MediaCommitResult original = await CommitBytesAsync(store, item, Payload);
        byte[] changed = Payload.Reverse().ToArray();
        if (removeFile)
            File.Delete(original.Path);
        else
            await File.WriteAllBytesAsync(original.Path, changed);

        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));

        MediaCommitResult repaired = await CommitBytesAsync(new MediaStore(), item, Payload);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(repaired.Path));
        if (!removeFile)
        {
            Assert.NotEqual(original.Path, repaired.Path);
            Assert.Equal(changed, await File.ReadAllBytesAsync(original.Path));
        }
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(repaired.Path, restored.Path);
    }

    [Fact]
    public async Task ExistingUnprovenTargetIsPreservedAndDoesNotSuppressTheDownload()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        byte[] foreign = [55, 66, 77];
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        await File.WriteAllBytesAsync(item.DestinationPath, foreign);
        MediaStore store = new MediaStore();

        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));

        MediaCommitResult committed = await CommitBytesAsync(store, item, Payload);
        Assert.NotEqual(item.DestinationPath, committed.Path);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(committed.Path));
        Assert.Equal(committed.Path, (await new MediaStore().TryRestoreAsync(item, CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task AChangedConflictFileDoesNotPreventASecondNonDestructiveRepair()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        byte[] originalForeign = [55, 66, 77];
        byte[] changedConflict = [88, 99, 100];
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        await File.WriteAllBytesAsync(item.DestinationPath, originalForeign);
        MediaCommitResult first = await CommitBytesAsync(new MediaStore(), item, Payload);
        await File.WriteAllBytesAsync(first.Path, changedConflict);

        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        MediaCommitResult repaired = await CommitBytesAsync(new MediaStore(), item, Payload);

        Assert.NotEqual(item.DestinationPath, repaired.Path);
        Assert.NotEqual(first.Path, repaired.Path);
        Assert.Equal(originalForeign, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Equal(changedConflict, await File.ReadAllBytesAsync(first.Path));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(repaired.Path));
        Assert.Equal(repaired.Path, (await new MediaStore().TryRestoreAsync(item, CancellationToken.None))?.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFileRequiresAProviderHashAndIsNeverMovedOrDeleted(bool provideHash)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        string legacyPath = Path.Combine(directory.Path, "old-name.bin");
        await File.WriteAllBytesAsync(legacyPath, Payload);
        DownloadItem item = CreateItem(board, directory, provideHash: provideHash, legacyPath: legacyPath);
        MediaStore store = new MediaStore();

        MediaCommitResult? restored = await store.TryRestoreAsync(item, CancellationToken.None);

        if (provideHash)
        {
            Assert.NotNull(restored);
            Assert.True(restored.Reused);
            Assert.Equal(item.DestinationPath, restored.Path);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(restored.Path));
        }
        else
        {
            Assert.Null(restored);
            Assert.False(File.Exists(item.DestinationPath));
            await CommitBytesAsync(store, item, Payload);
        }
        Assert.Equal(Payload, await File.ReadAllBytesAsync(legacyPath));
    }

    [Fact]
    public async Task FailedFilePublicationReportsFailureAndRetrySucceeds()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory);
        int failures = 0;
        MediaStore store = new MediaStore((source, destination, overwrite) =>
        {
            if (!IsManifest(destination) && Interlocked.Increment(ref failures) == 1)
                throw new IOException("Synthetic publication failure.");
            File.Move(source, destination, overwrite);
        });

        await Assert.ThrowsAsync<IOException>(() => CommitBytesAsync(store, item, Payload));

        Assert.False(File.Exists(item.DestinationPath));
        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        MediaCommitResult committed = await CommitBytesAsync(store, item, Payload);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(committed.Path));
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestFailureRetainsCompleteFileButRequiresProofBeforeReuse(bool provideHash)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: provideHash);
        MediaStore store = new MediaStore((source, destination, overwrite) =>
        {
            if (IsManifest(destination))
                throw new IOException("Synthetic manifest publication failure.");
            File.Move(source, destination, overwrite);
        });

        await Assert.ThrowsAsync<IOException>(() => CommitBytesAsync(store, item, Payload));

        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        MediaStore restarted = new MediaStore();
        MediaCommitResult? restored = await restarted.TryRestoreAsync(item, CancellationToken.None);
        if (provideHash)
        {
            Assert.NotNull(restored);
            Assert.True(restored.Reused);
        }
        else
        {
            Assert.Null(restored);
            await CommitBytesAsync(restarted, item, Payload);
        }
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManifestFailureAtConflictDestinationRequiresProviderProofForOfflineRecovery(
        bool provideHash, bool numberedConflict)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: provideHash);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        byte[] foreign = [55, 66, 77];
        await File.WriteAllBytesAsync(item.DestinationPath, foreign);
        string firstConflictPath = Path.Combine(threadDirectory, item.MediaIdentifier + "."
            + Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant() + ".bin");
        if (numberedConflict)
        {
            await File.WriteAllBytesAsync(firstConflictPath, foreign);
        }

        string? publishedPath = null;
        MediaStore failingStore = new MediaStore((source, destination, overwrite) =>
        {
            if (IsManifest(destination))
            {
                throw new IOException("Synthetic manifest publication failure after a conflict file.");
            }

            File.Move(source, destination, overwrite);
            publishedPath = destination;
        });

        await Assert.ThrowsAsync<IOException>(() => CommitBytesAsync(failingStore, item, Payload));
        Assert.NotNull(publishedPath);
        Assert.NotEqual(item.DestinationPath, publishedPath);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(publishedPath));
        Assert.False(File.Exists(Path.Combine(threadDirectory, ".ochan-manifest.json")));

        // No source stream is available after restart. A provider hash can still recover the file.
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);

        if (provideHash)
        {
            Assert.NotNull(restored);
            Assert.True(restored.Reused);
            Assert.Equal(publishedPath, restored.Path);
            Assert.Equal(publishedPath, (await new MediaStore().TryRestoreAsync(item, CancellationToken.None))?.Path);
        }
        else
        {
            Assert.Null(restored);
            Assert.False(File.Exists(Path.Combine(threadDirectory, ".ochan-manifest.json")));
        }

        Assert.Equal(foreign, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(publishedPath));
        if (numberedConflict)
        {
            Assert.NotEqual(firstConflictPath, publishedPath);
            Assert.Equal(foreign, await File.ReadAllBytesAsync(firstConflictPath));
        }
    }

    [Fact]
    public async Task OrphanRecoveryRejectsUnrelatedNamesAndUnverifiedConflictContent()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        string sha256 = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>
        {
            [Path.Combine(threadDirectory, "789." + sha256 + ".bin")] = Payload,
            [Path.Combine(threadDirectory, "456." + sha256 + ".draft.bin")] = Payload,
            [Path.Combine(threadDirectory, "456." + new string('0', 64) + ".bin")] = Payload,
            [Path.Combine(threadDirectory, "456." + sha256 + ".bin")] = Payload.Reverse().ToArray(),
            [Path.Combine(threadDirectory, "nested", "456." + sha256 + ".bin")] = Payload
        };
        foreach (KeyValuePair<string, byte[]> file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Key)!);
            await File.WriteAllBytesAsync(file.Key, file.Value);
        }

        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(threadDirectory, ".ochan-manifest.json")));
        foreach (KeyValuePair<string, byte[]> file in files)
        {
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key));
        }
    }

    [Fact]
    public async Task MissingManifestDoesNotTurnFileExistenceIntoCompletion()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        File.Delete(Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".ochan-manifest.json"));

        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));

        await CommitBytesAsync(new MediaStore(), item, Payload);
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task CorruptManifestIsPreservedWhileFreshContentRebuildsCompletionProof()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        string manifestPath = Path.Combine(threadDirectory, ".ochan-manifest.json");
        const string corruptJson = "{ broken json";
        await File.WriteAllTextAsync(manifestPath, corruptJson);

        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(manifestPath));

        await CommitBytesAsync(new MediaStore(), item, Payload);

        string backupPath = Assert.Single(Directory.GetFiles(threadDirectory, ".ochan-manifest.json.corrupt-*"));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(backupPath));
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownFutureManifestVersionIsNeverOverwritten()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        string manifestPath = Path.Combine(threadDirectory, ".ochan-manifest.json");
        const string futureJson = "{\"Version\":999,\"Entries\":{}}";
        await File.WriteAllTextAsync(manifestPath, futureJson);

        await Assert.ThrowsAsync<InvalidDataException>(() => new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => CommitBytesAsync(new MediaStore(), item, Payload));

        Assert.Equal(futureJson, await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task ManifestCannotAdoptAFileOutsideTheThreadDirectory()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        string threadDirectory = Path.GetDirectoryName(item.DestinationPath)!;
        Directory.CreateDirectory(threadDirectory);
        string outsidePath = Path.Combine(Path.GetDirectoryName(threadDirectory)!, "outside.bin");
        await File.WriteAllBytesAsync(outsidePath, Payload);
        Dictionary<string, object> entries = new Dictionary<string, object>
        {
            [Path.GetFileName(item.DestinationPath)] = new
            {
                MediaIdentifier = item.MediaIdentifier,
                SourceUrl = item.DownloadUri.ToString(),
                FileName = "../outside.bin",
                Length = Payload.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(Payload)),
                Md5 = (string?)null,
                OriginalFileName = item.OriginalFileName
            }
        };
        string manifest = JsonSerializer.Serialize(new { Version = 1, Entries = entries });
        await File.WriteAllTextAsync(Path.Combine(threadDirectory, ".ochan-manifest.json"), manifest);

        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outsidePath));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task VerificationCacheUsesAFixedMonotonicDeadlineAndDoesNotSurviveRestart()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        ManualVerificationTime time = new ManualVerificationTime();
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move, timeProvider: time,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));

        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(1, hashes);
        time.Advance(TimeSpan.FromMinutes(9));
        time.ShiftWallClock(TimeSpan.FromDays(50));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        time.ShiftWallClock(TimeSpan.FromDays(-100));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(1, hashes);

        // Hits at minute nine must not renew the original ten-minute deadline.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);

        MediaStore restarted = new MediaStore(File.Move, timeProvider: time,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await restarted.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(3, hashes);
    }

    [Fact]
    public async Task TimestampPreservingSameSizeChangeIsDetectedAtTheOriginalDeadline()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        ManualVerificationTime time = new ManualVerificationTime();
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move, timeProvider: time,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        DateTime lastWrite = File.GetLastWriteTimeUtc(item.DestinationPath);
        DateTime creation = File.GetCreationTimeUtc(item.DestinationPath);

        await File.WriteAllBytesAsync(item.DestinationPath, Payload.Reverse().ToArray());
        File.SetLastWriteTimeUtc(item.DestinationPath, lastWrite);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(item.DestinationPath));
        Assert.Equal(creation, File.GetCreationTimeUtc(item.DestinationPath));
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(1, hashes);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
        // An unsuccessful full verification must not create another ten minutes of trust.
        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(3, hashes);
    }

    [Fact]
    public async Task ChangedLastWriteTimeImmediatelyRequiresAFullVerification()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        DateTime originalTime = File.GetLastWriteTimeUtc(item.DestinationPath);

        File.SetLastWriteTimeUtc(item.DestinationPath, originalTime.AddMinutes(1));

        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("different-length")]
    [InlineData("missing-manifest")]
    [InlineData("missing-entry")]
    public async Task MissingOrChangedRestoreInputsImmediatelyDiscardCachedProof(string change)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        string manifestPath = Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".ochan-manifest.json");
        string manifest = await File.ReadAllTextAsync(manifestPath);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));

        switch (change)
        {
            case "missing-file":
                File.Delete(item.DestinationPath);
                break;
            case "different-length":
                await File.WriteAllBytesAsync(item.DestinationPath, [1]);
                break;
            case "missing-manifest":
                File.Delete(manifestPath);
                break;
            case "missing-entry":
                await File.WriteAllTextAsync(manifestPath, "{\"Version\":1,\"Entries\":{}}");
                break;
        }

        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(1, hashes);
        if (change is "missing-file" or "different-length")
        {
            await File.WriteAllBytesAsync(item.DestinationPath, Payload);
        }
        else
        {
            await File.WriteAllTextAsync(manifestPath, manifest);
        }

        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedExpectedMetadataInvalidatesCachedProof(bool changeMd5)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        DownloadItem changed = new DownloadItem(item.DownloadUri, item.DestinationPath, board, item.Thread,
            item.MediaIdentifier, expectedSize: changeMd5 ? Payload.Length : null,
            expectedMd5: changeMd5 ? Convert.ToBase64String(MD5.HashData(Payload)) : null);

        Assert.NotNull(await store.TryRestoreAsync(changed, CancellationToken.None));
        Assert.Equal(2, hashes);
        Assert.NotNull(await store.TryRestoreAsync(changed, CancellationToken.None));
        Assert.Equal(2, hashes);

        // Leave ExpectedSize absent for a wrong MD5 so no orphan recovery is attempted.
        DownloadItem invalid = new DownloadItem(item.DownloadUri, item.DestinationPath, board, item.Thread,
            item.MediaIdentifier, expectedSize: changeMd5 ? null : Payload.Length + 1,
            expectedMd5: changeMd5 ? Convert.ToBase64String(MD5.HashData([99])) : null);
        Assert.Null(await store.TryRestoreAsync(invalid, CancellationToken.None));
        int afterRejection = hashes;
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(afterRejection + 1, hashes);
    }

    [Theory]
    [InlineData("SourceUrl")]
    [InlineData("OriginalFileName")]
    [InlineData("ExpectedMd5")]
    [InlineData("Md5")]
    public async Task AnyChangedManifestMetadataRequiresAFullVerification(string field)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        string manifestPath = Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".ochan-manifest.json");
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["Entries"]![Path.GetFileName(item.DestinationPath)]![field] = "changed";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public async Task ManifestChecksumChangeCannotReuseThePreviousCachedProof()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        string manifestPath = Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".ochan-manifest.json");
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["Entries"]![Path.GetFileName(item.DestinationPath)]!["Sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public async Task CancelledVerificationCannotPopulateTheCache()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move, onFileHashStarting: _ =>
        {
            if (Interlocked.Increment(ref hashes) == 1)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.TryRestoreAsync(item, cancellation.Token));

        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public async Task FileStampChangingDuringVerificationCannotPopulateTheCache()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem item = CreateItem(board, directory, provideHash: false);
        await CommitBytesAsync(new MediaStore(), item, Payload);
        int hashes = 0;
        MediaStore store = new MediaStore(File.Move, onFileHashStarting: path =>
        {
            if (Interlocked.Increment(ref hashes) == 1)
            {
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
            }
        });

        Assert.Null(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
        Assert.NotNull(await store.TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public async Task VerificationCacheEvictsProofInsteadOfGrowingPastItsCapacity()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem[] items = Enumerable.Range(1, 3)
            .Select(index => CreateItem(board, directory, index.ToString(), provideHash: false)).ToArray();
        MediaStore writer = new MediaStore();
        foreach (DownloadItem item in items)
        {
            await CommitBytesAsync(writer, item, Payload);
        }

        int hashes = 0;
        MediaStore reader = new MediaStore(File.Move, verificationCacheCapacity: 2,
            onFileHashStarting: _ => Interlocked.Increment(ref hashes));
        foreach (DownloadItem item in items)
        {
            Assert.NotNull(await reader.TryRestoreAsync(item, CancellationToken.None));
        }

        Assert.Equal(3, hashes);
        foreach (DownloadItem item in items)
        {
            Assert.NotNull(await reader.TryRestoreAsync(item, CancellationToken.None));
        }

        Assert.True(hashes > 3, "A bounded cache must fully verify at least one evicted file again.");
    }

    private sealed class ManualVerificationTime : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            _timestamp += elapsed.Ticks;
            _utcNow += elapsed;
        }

        public void ShiftWallClock(TimeSpan offset) => _utcNow += offset;
    }

    private static bool IsManifest(string path) => Path.GetFileName(path) == ".ochan-manifest.json";

    private static async Task<MediaCommitResult> CommitBytesAsync(MediaStore store, DownloadItem item, byte[] payload)
    {
        using MemoryStream source = new MemoryStream(payload, writable: false);
        return await store.CommitAsync(item, source, CancellationToken.None);
    }

    private static DownloadItem CreateItem(StubImageBoard board, TempDirectory directory, string mediaId = "456",
        bool provideHash = true, string? legacyPath = null)
    {
        return CreateItem(board, directory.Path, mediaId, provideHash, legacyPath);
    }

    private static DownloadItem CreateItem(StubImageBoard board, string root, string mediaId = "456",
        bool provideHash = true, string? legacyPath = null)
    {
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/123"));
        return new DownloadItem(new Uri($"https://stub.test/media/{mediaId}.bin"),
            Path.Combine(root, "stub", "g", "123", mediaId + ".bin"), board, thread, mediaId,
            expectedSize: Payload.Length, expectedMd5: provideHash ? Convert.ToBase64String(MD5.HashData(Payload)) : null,
            originalFileName: "same-name.bin", legacyPath: legacyPath);
    }
}

internal sealed class PartialFailureStream(byte[] payload, Action? beforeFailure = null) : MemoryStream(payload, writable: false)
{
    private bool _readPrefix;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_readPrefix)
        {
            _readPrefix = true;
            return base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        }
        beforeFailure?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Synthetic failure after a partial body.");
    }
}

internal sealed class CancelAtEndStream(byte[] payload, Action onEnd) : MemoryStream(payload, writable: false)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = await base.ReadAsync(buffer, cancellationToken);
        if (bytesRead == 0)
        {
            onEnd();
        }

        return bytesRead;
    }
}

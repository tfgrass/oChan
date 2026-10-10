using System.Net;
using System.Threading.Channels;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;

namespace oChan.Tests;

public sealed class ConsumerContractTests
{
    private const long UnthrottledBandwidth = 1L << 30;

    [Fact]
    public async Task BlockedStateConsumerDoesNotBlockAnotherConsumerAdmissionDownloadsOrStop()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        await using Registry registry = CreateRegistry(provider, queue, directory.Path);
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource exited = NewCompletion();
        TaskCompletionSource healthy = NewCompletion();
        int timedOut = 0;
        registry.StateChanged += () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TestWait.Timeout)) Interlocked.Exchange(ref timedOut, 1);
            exited.TrySetResult();
        };
        registry.StateChanged += () => healthy.TrySetResult();

        try
        {
            AdmissionResult first = await Task.Run(() => registry.AddAsync("https://stub.test/g/thread/1"))
                .WaitAsync(TestWait.Timeout);
            await entered.Task.WaitAsync(TestWait.Timeout);
            await healthy.Task.WaitAsync(TestWait.Timeout);

            AdmissionResult second = await Task.Run(() => registry.AddAsync("https://stub.test/g/thread/2"))
                .WaitAsync(TestWait.Timeout);
            Assert.Equal(AdmissionStatus.Added, second.Status);
            ProbeThread thread = Assert.IsType<ProbeThread>(first.Thread);
            thread.TotalMediaCount = 1;
            DownloadItem item = CreateItem(provider, thread, directory, 1);
            Assert.True(await Task.Run(() => queue.TryEnqueue(item)).WaitAsync(TestWait.Timeout));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.True(File.Exists(item.DestinationPath));

            await Task.Run(registry.StopAsync).WaitAsync(TestWait.Timeout);
            DownloadSessionSnapshot stopped = registry.GetSnapshot();
            Assert.True(stopped.IsStopping);
            Assert.Empty(stopped.Threads);
            Assert.False(exited.Task.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref timedOut));
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted) await exited.Task.WaitAsync(TestWait.Timeout);
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ThrowingStateConsumerDoesNotBreakDownloadsOrFinalStateDelivery()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        await using Registry registry = CreateRegistry(provider, queue, directory.Path);
        TaskCompletionSource failedConsumerCalled = NewCompletion();
        TaskCompletionSource<ThreadSnapshot> completed = new TaskCompletionSource<ThreadSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        registry.StateChanged += () =>
        {
            failedConsumerCalled.TrySetResult();
            throw new InvalidOperationException("Synthetic consumer failure");
        };
        registry.StateChanged += () =>
        {
            ThreadSnapshot? thread = registry.GetSnapshot().Threads.SingleOrDefault();
            if (thread is { CompletedMedia: 1, Activity: ThreadActivity.Finished }
                && thread.Downloads == new DownloadCounts(0, 0))
                completed.TrySetResult(thread);
        };

        AdmissionResult result = await registry.AddAsync("https://stub.test/g/thread/1")
            .WaitAsync(TestWait.Timeout);
        await failedConsumerCalled.Task.WaitAsync(TestWait.Timeout);
        ProbeThread admitted = Assert.IsType<ProbeThread>(result.Thread);
        admitted.TotalMediaCount = 1;
        Assert.True(queue.TryEnqueue(CreateItem(provider, admitted, directory, 1)));
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        ThreadSnapshot observed = await completed.Task.WaitAsync(TestWait.Timeout);
        Assert.Empty(observed.MediaFailures);
        Assert.Null(observed.LastCheckFailure);
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
        Assert.True(registry.GetSnapshot().IsStopping);
    }

    [Fact]
    public async Task SessionSnapshotsAreReadOnlyAndRemainStableAfterResourceChangesAndRemoval()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), directory.Path);
        await registry.AddAsync("https://stub.test/g/").WaitAsync(TestWait.Timeout);
        AdmissionResult admitted = await registry.AddAsync("https://stub.test/g/thread/123/title#post")
            .WaitAsync(TestWait.Timeout);
        ProbeThread thread = Assert.IsType<ProbeThread>(admitted.Thread);
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1 && provider.Board.Clock.Waits == 1);
        thread.Title = "Before";
        thread.TotalMediaCount = 1;
        thread.ReportMediaFailure("media-1", new HttpRequestException("Missing media", null, HttpStatusCode.NotFound));

        DownloadSessionSnapshot before = registry.GetSnapshot();
        ThreadSnapshot oldThread = Assert.Single(before.Threads);
        BoardSnapshot oldBoard = Assert.Single(before.Boards);
        Assert.Equal(new ResourceId("stub", "g", "123"), oldThread.Id);
        Assert.Equal("https://stub.test/g/thread/123", oldThread.Url);
        Assert.Equal(Path.Combine(directory.Path, "stub", "g", "123"), oldThread.DownloadDirectory);
        Assert.Equal(new ResourceId("stub", "g"), oldBoard.Id);
        Assert.Equal(Path.Combine(directory.Path, "stub", "g"), oldBoard.DownloadDirectory);
        AssertReadOnly(before.Threads);
        AssertReadOnly(before.Boards);
        AssertReadOnly(before.Warnings);
        if (oldThread.MediaFailures is IDictionary<string, FailureInfo> failures)
        {
            Assert.True(failures.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => failures.Clear());
            Assert.Throws<NotSupportedException>(() => failures["media-1"] = new FailureInfo(FailureKind.Unknown, "Changed"));
        }

        thread.Title = "After";
        thread.MarkMediaAsDownloaded("media-1");
        ThreadSnapshot changed = Assert.Single(registry.GetSnapshot().Threads);
        Assert.Equal("After", changed.Title);
        Assert.Equal(ThreadActivity.Finished, changed.Activity);
        Assert.Equal(1, changed.CompletedMedia);
        Assert.Empty(changed.MediaFailures);
        await registry.RemoveThreadAsync(thread).WaitAsync(TestWait.Timeout);
        await registry.RemoveBoardAsync(provider.Board).WaitAsync(TestWait.Timeout);

        DownloadSessionSnapshot empty = registry.GetSnapshot();
        Assert.Empty(empty.Threads);
        Assert.Empty(empty.Boards);
        Assert.Same(oldThread, Assert.Single(before.Threads));
        Assert.Same(oldBoard, Assert.Single(before.Boards));
        Assert.Equal("Before", oldThread.Title);
        Assert.Equal(ThreadActivity.Error, oldThread.Activity);
        Assert.Equal(0, oldThread.CompletedMedia);
        Assert.Equal(HttpStatusCode.NotFound, Assert.Single(oldThread.MediaFailures).Value.HttpStatusCode);
        Assert.False(before.IsStopping);
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
        Assert.False(before.IsStopping);
        Assert.True(registry.GetSnapshot().IsStopping);
    }

    [Fact]
    public async Task WarningSnapshotsKeepOnlyTheLatestThirtyTwoWithoutChangingEarlierCopies()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        string[] urls = Enumerable.Range(0, 40)
            .Select(index => $"https://missing.test/g/thread/{index}").ToArray();
        ReadOnlySessionStore store = new ReadOnlySessionStore(new SessionSnapshot { ThreadUrls = urls });
        await using Registry registry = new Registry([provider], new DownloadQueue(1, UnthrottledBandwidth),
            directory.Path, TimeSpan.FromMinutes(1), 60, store, persistSession: false);
        DownloadSessionSnapshot before = registry.GetSnapshot();

        await registry.RestoreAsync().WaitAsync(TestWait.Timeout);

        DownloadSessionSnapshot restored = registry.GetSnapshot();
        Assert.Empty(before.Warnings);
        Assert.Equal(32, restored.Warnings.Count);
        for (int index = 0; index < restored.Warnings.Count; index++)
            Assert.Contains(urls[index + 8], restored.Warnings[index]);
        AssertReadOnly(restored.Warnings);
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(restored.Warnings, registry.GetSnapshot().Warnings);
        Assert.Equal(32, restored.Warnings.Count);
    }

    [Fact]
    public async Task RegistryCountsAndNotificationsFollowTheCurrentWatchInstanceAcrossReplacement()
    {
        using TempDirectory directory = new TempDirectory();
        HeldMediaHandler handler = new HeldMediaHandler();
        using StubImageBoard provider = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        await using Registry registry = CreateRegistry(provider, queue, directory.Path);
        AdmissionResult first = await registry.AddAsync("https://stub.test/g/thread/1").WaitAsync(TestWait.Timeout);
        AdmissionResult second = await registry.AddAsync("https://stub.test/g/thread/2").WaitAsync(TestWait.Timeout);
        IThread original = Assert.IsAssignableFrom<IThread>(first.Thread);
        IThread other = Assert.IsAssignableFrom<IThread>(second.Thread);
        TaskCompletionSource<DownloadSessionSnapshot> countsChanged = new TaskCompletionSource<DownloadSessionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        registry.StateChanged += () =>
        {
            DownloadSessionSnapshot snapshot = registry.GetSnapshot();
            if (snapshot.Threads.Any(thread => thread.Id.Thread == "1" && thread.Downloads == new DownloadCounts(1, 1))
                && snapshot.Threads.Any(thread => thread.Id.Thread == "2" && thread.Downloads == new DownloadCounts(1, 0)))
                countsChanged.TrySetResult(snapshot);
        };

        Assert.True(queue.TryEnqueue(CreateItem(provider, original, directory, 1)));
        await handler.WaitForStartAsync();
        Assert.True(queue.TryEnqueue(CreateItem(provider, original, directory, 2)));
        Assert.True(queue.TryEnqueue(CreateItem(provider, other, directory, 1)));
        DownloadSessionSnapshot occupied = await countsChanged.Task.WaitAsync(TestWait.Timeout);
        await registry.RemoveThreadAsync(original).WaitAsync(TestWait.Timeout);
        await handler.WaitForStartAsync();
        AdmissionResult readmitted = await registry.AddAsync(original.Url).WaitAsync(TestWait.Timeout);
        IThread replacement = Assert.IsAssignableFrom<IThread>(readmitted.Thread);
        Assert.NotSame(original, replacement);
        await registry.RemoveThreadAsync(original).WaitAsync(TestWait.Timeout);

        DownloadSessionSnapshot current = registry.GetSnapshot();
        Assert.Equal(new DownloadCounts(0, 0), Assert.Single(current.Threads, thread => thread.Id.Thread == "1").Downloads);
        Assert.Equal(new DownloadCounts(0, 1), Assert.Single(current.Threads, thread => thread.Id.Thread == "2").Downloads);
        Assert.Equal(new DownloadCounts(1, 1), Assert.Single(occupied.Threads, thread => thread.Id.Thread == "1").Downloads);
        Assert.Equal(new DownloadCounts(1, 0), Assert.Single(occupied.Threads, thread => thread.Id.Thread == "2").Downloads);
        Assert.Contains(registry.Threads, thread => ReferenceEquals(thread, replacement));
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public void MediaFailureSnapshotRetainsTypedHttpFailureUntilTheSameMediaSucceeds()
    {
        using StubImageBoard provider = new StubImageBoard();
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/1"));
        thread.TotalMediaCount = 2;
        thread.ReportMediaFailure("missing", new HttpRequestException("Media returned 404", null, HttpStatusCode.NotFound));
        thread.MarkMediaAsDownloaded("other");

        ThreadSnapshot failed = thread.GetSnapshot(new DownloadCounts(0, 0));
        FailureInfo failure = Assert.Single(failed.MediaFailures).Value;
        Assert.Equal(FailureKind.Http, failure.Kind);
        Assert.Equal(HttpStatusCode.NotFound, failure.HttpStatusCode);
        Assert.Equal("Media returned 404", failure.Message);
        Assert.Equal(ThreadActivity.Error, failed.Activity);
        Assert.Equal(2, failed.KnownMedia);
        Assert.Equal(1, failed.CompletedMedia);
        Assert.Null(failed.LastCheckFailure);

        thread.MarkMediaAsDownloaded("missing");
        ThreadSnapshot recovered = thread.GetSnapshot(new DownloadCounts(0, 0));
        Assert.Equal(ThreadActivity.Finished, recovered.Activity);
        Assert.Equal(2, recovered.CompletedMedia);
        Assert.Empty(recovered.MediaFailures);
        Assert.Equal(ThreadActivity.Error, failed.Activity);
        Assert.Single(failed.MediaFailures);
    }

    [Theory]
    [InlineData(true, FailureKind.InvalidData)]
    [InlineData(false, FailureKind.Network)]
    public async Task ThreadPollingSnapshotClassifiesFailureAndClearsItAfterSuccessfulCheck(bool invalidData, FailureKind expected)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/1"));
        Exception failure = invalidData
            ? new InvalidDataException("Synthetic malformed response")
            : new HttpRequestException("Synthetic network failure");
        thread.OnCheck = () => Task.FromException(failure);
        provider.ThreadFactory = _ => thread;
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), directory.Path);
        await registry.AddAsync(thread.Url).WaitAsync(TestWait.Timeout);
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1);

        ThreadSnapshot failed = Assert.Single(registry.GetSnapshot().Threads);
        Assert.Equal(ThreadActivity.Error, failed.Activity);
        Assert.Equal(expected, failed.LastCheckFailure?.Kind);
        Assert.Equal(failure.Message, failed.LastCheckFailure?.Message);
        Assert.Null(failed.LastCheckFailure?.HttpStatusCode);
        Assert.Empty(failed.MediaFailures);

        thread.OnCheck = () => { thread.Activity = ThreadActivity.NoMedia; return Task.CompletedTask; };
        thread.Clock.Advance();
        await TestWait.UntilAsync(() => thread.Clock.Waits == 2);
        ThreadSnapshot recovered = Assert.Single(registry.GetSnapshot().Threads);
        Assert.Equal(ThreadActivity.NoMedia, recovered.Activity);
        Assert.Null(recovered.LastCheckFailure);
        Assert.Equal(expected, failed.LastCheckFailure?.Kind);
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
    }

    [Theory]
    [InlineData(true, FailureKind.InvalidData)]
    [InlineData(false, FailureKind.Network)]
    public async Task BoardPollingSnapshotClassifiesFailureAndClearsItAfterSuccessfulCheck(bool invalidData, FailureKind expected)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        Exception failure = invalidData
            ? new InvalidDataException("Synthetic malformed catalog")
            : new HttpRequestException("Synthetic catalog network failure");
        provider.Board.OnCheck = () => Task.FromException<IEnumerable<IThread>>(failure);
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), directory.Path);
        await registry.AddAsync(provider.Board.BoardUri.AbsoluteUri).WaitAsync(TestWait.Timeout);
        await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 1);

        BoardSnapshot failed = Assert.Single(registry.GetSnapshot().Boards);
        Assert.Equal(expected, failed.LastCheckFailure?.Kind);
        Assert.Equal(failure.Message, failed.LastCheckFailure?.Message);
        Assert.Null(failed.LastCheckFailure?.HttpStatusCode);
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>([]);
        provider.Board.Clock.Advance();
        await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 2);
        Assert.Null(Assert.Single(registry.GetSnapshot().Boards).LastCheckFailure);
        Assert.Equal(expected, failed.LastCheckFailure?.Kind);
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedSecondOwnerCannotStopTheFirstSessionsResource(bool boardWatch)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ProbeThread sharedThread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/1"));
        provider.ThreadFactory = _ => sharedThread;
        await using Registry first = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), Path.Combine(directory.Path, "first"));
        await using Registry second = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), Path.Combine(directory.Path, "second"));
        string url = boardWatch ? provider.Board.BoardUri.AbsoluteUri : sharedThread.Url;
        AsyncStepGate clock = boardWatch ? provider.Board.Clock : sharedThread.Clock;

        await first.AddAsync(url).WaitAsync(TestWait.Timeout);
        await TestWait.UntilAsync(() => clock.Waits == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.AddAsync(url).WaitAsync(TestWait.Timeout));
        Assert.Empty(second.GetSnapshot().Threads);
        Assert.Empty(second.GetSnapshot().Boards);
        await second.StopAsync().WaitAsync(TestWait.Timeout);
        clock.Advance();
        await TestWait.UntilAsync(() => clock.Waits == 2,
            "Rejecting a second owner must preserve the first owner's polling lifetime.");
        if (boardWatch) Assert.Single(first.GetSnapshot().Boards);
        else Assert.Single(first.GetSnapshot().Threads);
        await first.StopAsync().WaitAsync(TestWait.Timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerLeaseRemainsExclusiveUntilCancelledResourceCleanupHasFinished(bool boardWatch)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ProbeThread sharedThread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/1"));
        provider.ThreadFactory = _ => sharedThread;
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource cancellationObserved = NewCompletion();
        TaskCompletionSource releaseCleanup = NewCompletion();
        int checks = 0;
        async Task HoldFirstCheckAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref checks) != 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                await releaseCleanup.Task.WaitAsync(TestWait.Timeout);
                throw;
            }
        }
        sharedThread.OnCheckWithCancellation = HoldFirstCheckAsync;
        provider.Board.OnCheckWithCancellation = async cancellationToken =>
        {
            await HoldFirstCheckAsync(cancellationToken);
            return [];
        };
        await using Registry first = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), Path.Combine(directory.Path, "first"));
        await using Registry second = CreateRegistry(provider, new DownloadQueue(1, UnthrottledBandwidth), Path.Combine(directory.Path, "second"));
        string url = boardWatch ? provider.Board.BoardUri.AbsoluteUri : sharedThread.Url;

        try
        {
            await first.AddAsync(url).WaitAsync(TestWait.Timeout);
            await entered.Task.WaitAsync(TestWait.Timeout);
            Task removal = boardWatch ? first.RemoveBoardAsync(provider.Board) : first.RemoveThreadAsync(sharedThread);
            await cancellationObserved.Task.WaitAsync(TestWait.Timeout);
            Assert.False(removal.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.AddAsync(url).WaitAsync(TestWait.Timeout));
            Assert.False(removal.IsCompleted);
            releaseCleanup.TrySetResult();
            await removal.WaitAsync(TestWait.Timeout);

            AdmissionResult replacement = await second.AddAsync(url).WaitAsync(TestWait.Timeout);
            Assert.Equal(AdmissionStatus.Added, replacement.Status);
            await TestWait.UntilAsync(() => Volatile.Read(ref checks) == 2);
            if (boardWatch) Assert.Same(provider.Board, replacement.Board);
            else Assert.Same(sharedThread, replacement.Thread);
            Assert.Empty(first.GetSnapshot().Threads);
            Assert.Empty(first.GetSnapshot().Boards);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await first.StopAsync().WaitAsync(TestWait.Timeout);
            await second.StopAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private static Registry CreateRegistry(StubImageBoard provider, DownloadQueue queue, string directory)
        => new Registry([provider], queue, directory, TimeSpan.FromMinutes(1), 60);

    private static TaskCompletionSource NewCompletion()
        => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private static DownloadItem CreateItem(StubImageBoard provider, IThread thread, TempDirectory directory, int mediaNumber)
        => new DownloadItem(new Uri($"https://stub.test/media/{thread.ThreadId}/{mediaNumber}.bin"),
            Path.Combine(directory.Path, thread.ThreadId, $"{mediaNumber}.bin"), provider, thread, $"media-{mediaNumber}");

    private static void AssertReadOnly<T>(IReadOnlyList<T> snapshot)
    {
        if (snapshot is not IList<T> mutable) return;
        Assert.True(mutable.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutable.Clear());
        if (mutable.Count > 0)
            Assert.Throws<NotSupportedException>(() => mutable[0] = mutable[0]);
    }

    private sealed class ReadOnlySessionStore(SessionSnapshot snapshot) : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLoadResult(snapshot, null, false));

        public Task SaveAsync(SessionSnapshot value, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This test session must not save state.");
    }

    private sealed class HeldMediaHandler : HttpMessageHandler
    {
        private readonly Channel<bool> _starts = Channel.CreateUnbounded<bool>();

        public async Task WaitForStartAsync()
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TestWait.Timeout);
            await _starts.Reader.ReadAsync(timeout.Token);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _starts.Writer.TryWrite(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The synthetic media request must finish through cancellation.");
        }
    }
}

using System.Net;
using System.Threading.Channels;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class DownloadQueueTests
{
    private const long UnthrottledBandwidth = 1L << 30;
    private static readonly byte[] Payload = [1, 3, 5, 7, 9];

    [Fact]
    public async Task ManyThreadsShareTheConfiguredWorkerLimit()
    {
        const int workerLimit = 3;
        const int jobCount = 24;
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(workerLimit, UnthrottledBandwidth);
        DownloadItem[] items = Enumerable.Range(1, jobCount)
            .Select(id => CreateItem(board, Thread(board, id), directory, $"{id}/media.bin", $"media-{id}"))
            .ToArray();

        try
        {
            bool[] admissions = await Task.WhenAll(items.Select(item => Task.Run(() => queue.TryEnqueue(item))))
                .WaitAsync(TestWait.Timeout);
            Assert.All(admissions, accepted => Assert.True(accepted));

            await handler.WaitForStartsAsync(workerLimit);
            Assert.Equal(workerLimit, handler.ActiveRequests);
            Assert.Equal(workerLimit, handler.RequestCount);
            Assert.False(queue.WaitForIdleAsync().IsCompleted);

            handler.Release(jobCount);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(jobCount, handler.RequestCount);
            Assert.Equal(workerLimit, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.ActiveRequests);
            foreach (DownloadItem item in items)
            {
                Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
                Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            }
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentEquivalentJobsAreAdmittedOnceWhileQueuedOrRunning(bool keepJobQueued)
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread thread = Thread(board, 1);
        DownloadItem item = CreateItem(board, thread, directory, "target/media.bin", "same-medium");

        try
        {
            if (keepJobQueued)
            {
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 2), directory, "blocker/media.bin", "blocker")));
                await handler.WaitForStartsAsync(1);
            }

            TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>[] attempts = Enumerable.Range(0, 32).Select(async attempt =>
            {
                await start.Task;
                // A refreshed URL still represents the same thread/medium/destination job.
                return queue.TryEnqueue(new DownloadItem(
                    new Uri($"https://stub.test/media/shared.bin?attempt={attempt}"),
                    item.DestinationPath, board, thread, item.MediaIdentifier));
            }).ToArray();
            start.SetResult();

            bool[] admissions = await Task.WhenAll(attempts).WaitAsync(TestWait.Timeout);
            Assert.Equal(1, admissions.Count(accepted => accepted));
            Assert.False(queue.TryEnqueue(item));

            if (keepJobQueued)
            {
                Assert.Equal(1, handler.RequestCount);
                handler.Release(1);
            }

            await handler.WaitForStartsAsync(1);
            Assert.False(queue.TryEnqueue(item));
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(keepJobQueued ? 2 : 1, handler.RequestCount);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task TheSameMediaUriCanBeDownloadedToSeparateLegitimateDestinations(bool useDifferentThread,
        int workerLimit)
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(workerLimit, UnthrottledBandwidth);
        IThread firstThread = Thread(board, 1);
        IThread secondThread = useDifferentThread ? Thread(board, 2) : firstThread;
        Uri sharedUri = new Uri("https://stub.test/media/shared.bin");
        DownloadItem first = CreateItem(board, firstThread, directory, "first/media.bin", "same-medium", sharedUri);
        DownloadItem second = CreateItem(board, secondThread, directory, "second/media.bin", "same-medium", sharedUri);

        try
        {
            Assert.True(queue.TryEnqueue(first));
            Assert.True(queue.TryEnqueue(second));
            if (workerLimit == 1)
            {
                await handler.WaitForStartsAsync(1);
                handler.Release(1);
                await handler.WaitForStartsAsync(1);
                Assert.True(firstThread.IsMediaDownloaded(first.MediaIdentifier));
                handler.Release(1);
            }
            else
            {
                await handler.WaitForStartsAsync(2);
                handler.Release(2);
            }
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(first.DestinationPath));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(second.DestinationPath));
            Assert.True(firstThread.IsMediaDownloaded(first.MediaIdentifier));
            Assert.True(secondThread.IsMediaDownloaded(second.MediaIdentifier));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task CompletedJobsReleaseTheirReservation()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "media.bin", "medium");

        try
        {
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            Assert.False(queue.TryEnqueue(item));
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.True(queue.TryEnqueue(item));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            // Reservation lifetime ends with the job; the worker may reuse its completed file.
            Assert.Equal(1, handler.RequestCount);
            Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task FailedJobsReleaseTheirReservationForAnotherAttempt()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler(HttpStatusCode.ServiceUnavailable);
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "media.bin", "medium");

        try
        {
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ThreadCancellationRejectsLateJobsAndKeepsItsIdentityUntilTheWorkerFinishes()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler(delayCancellationCompletion: true);
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread oldThread = Thread(board, 1);
        IThread newThread = Thread(board, 1);
        DownloadItem active = CreateItem(board, oldThread, directory, "old/active.bin", "active");
        DownloadItem pending = CreateItem(board, oldThread, directory, "old/pending.bin", "pending");
        DownloadItem unrelated = CreateItem(board, Thread(board, 2), directory, "other/media.bin", "other");
        DownloadItem replacement = CreateItem(board, newThread, directory, "old/active.bin", "active");
        DownloadItem newGenerationJob = CreateItem(board, newThread, directory, "new/media.bin", "new-medium");
        DownloadItem lateOldJob = CreateItem(board, oldThread, directory, "old/late.bin", "late");

        try
        {
            Assert.True(queue.TryEnqueue(active));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(pending));
            Assert.True(queue.TryEnqueue(unrelated));

            Task cancellation = queue.CancelDownloadsForThreadAsync(oldThread);
            await handler.WaitForCancellationAsync();

            Assert.False(cancellation.IsCompleted);
            Assert.False(queue.TryEnqueue(lateOldJob));
            Assert.False(queue.TryEnqueue(replacement));
            Assert.False(queue.TryEnqueue(newGenerationJob));
            Assert.Equal(1, handler.RequestCount);

            handler.CompleteCancellation();
            await cancellation.WaitAsync(TestWait.Timeout);
            await handler.WaitForStartsAsync(1);

            Assert.True(queue.TryEnqueue(replacement));
            Assert.False(queue.TryEnqueue(lateOldJob));
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(1, handler.CancellationCount);
            Assert.Equal(3, handler.RequestCount);
            Assert.False(File.Exists(pending.DestinationPath));
            Assert.False(oldThread.IsMediaDownloaded(active.MediaIdentifier));
            Assert.True(newThread.IsMediaDownloaded(replacement.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(unrelated.DestinationPath));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(replacement.DestinationPath));
        }
        finally
        {
            handler.CompleteCancellation();
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task RepeatedStopCallsAwaitTheSameOperationAndCloseAdmission()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler(delayCancellationCompletion: true);
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem active = CreateItem(board, Thread(board, 1), directory, "active.bin", "active");
        DownloadItem pending = CreateItem(board, Thread(board, 2), directory, "pending.bin", "pending");

        try
        {
            Assert.True(queue.TryEnqueue(active));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(pending));

            Task firstStop = queue.StopAllAsync();
            await handler.WaitForCancellationAsync();
            Task secondStop = queue.StopAllAsync();

            Assert.Same(firstStop, secondStop);
            Assert.False(firstStop.IsCompleted);
            Assert.False(queue.WaitForIdleAsync().IsCompleted);
            Assert.False(queue.TryEnqueue(active));
            Assert.False(queue.TryEnqueue(CreateItem(board, Thread(board, 3), directory, "late.bin", "late")));

            handler.CompleteCancellation();
            await Task.WhenAll(firstStop, secondStop).WaitAsync(TestWait.Timeout);

            Assert.Same(firstStop, queue.StopAllAsync());
            Assert.True(queue.WaitForIdleAsync().IsCompletedSuccessfully);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(1, handler.CancellationCount);
            Assert.Equal(0, handler.ActiveRequests);
            Assert.False(File.Exists(pending.DestinationPath));
            Assert.False(queue.TryEnqueue(pending));
        }
        finally
        {
            handler.CompleteCancellation();
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ResizingAnIdleQueueUsesTheFinalWorkerLimit()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(5, UnthrottledBandwidth);

        try
        {
            foreach (int workerLimit in new int[] { 5, 3, 6 })
            {
                queue.UpdateMaxParallelDownloads(workerLimit);
                Assert.Equal(workerLimit, queue.MaxParallelDownloads);
            }

            foreach (int id in Enumerable.Range(1, 6))
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, id), directory, $"{id}/media.bin", $"media-{id}")));

            await handler.WaitForStartsAsync(6);
            Assert.Equal(6, handler.ActiveRequests);
            handler.Release(6);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(6, handler.MaximumActiveRequests);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ReducingTheWorkerLimitWaitsForActiveJobsBeforeStartingMore()
    {
        const int jobCount = 8;
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(5, UnthrottledBandwidth);

        try
        {
            foreach (int id in Enumerable.Range(1, jobCount))
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, id), directory, $"{id}/media.bin", $"media-{id}")));
            await handler.WaitForStartsAsync(5);

            queue.UpdateMaxParallelDownloads(2);
            Assert.Equal(2, queue.MaxParallelDownloads);
            Assert.Equal(5, handler.ActiveRequests);

            for (int remainingActive = 4; remainingActive >= 2; remainingActive--)
            {
                handler.Release(1);
                Uri completedUri = await handler.WaitForReturnAsync();
                await TestWait.UntilAsync(() => !queue.IsInQueue(completedUri.ToString()));
                Assert.Equal(remainingActive, handler.ActiveRequests);
                Assert.Equal(5, handler.RequestCount);
                Assert.Equal(0, handler.CancellationCount);
            }

            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal(2, handler.ActiveRequests);
            Assert.Equal(6, handler.RequestCount);

            handler.Release(jobCount);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(jobCount, handler.RequestCount);
            Assert.Equal(5, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.CancellationCount);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private static IThread Thread(StubImageBoard board, int id)
        => board.GetThread(new Uri($"https://stub.test/g/thread/{id}"));

    private static DownloadItem CreateItem(StubImageBoard board, IThread thread, TempDirectory directory,
        string relativePath, string mediaIdentifier, Uri? downloadUri = null)
        => new DownloadItem(downloadUri ?? new Uri($"https://stub.test/media/{mediaIdentifier}.bin"),
            Path.Combine(directory.Path, relativePath), board, thread, mediaIdentifier);

    private sealed class GatedMediaHandler(HttpStatusCode statusCode = HttpStatusCode.OK,
        bool delayCancellationCompletion = false) : HttpMessageHandler
    {
        private readonly SemaphoreSlim _responses = new SemaphoreSlim(0);
        private readonly Channel<bool> _starts = Channel.CreateUnbounded<bool>();
        private readonly Channel<Uri> _returns = Channel.CreateUnbounded<Uri>();
        private readonly TaskCompletionSource _cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        private int _activeRequests;
        private int _maximumActiveRequests;
        private int _cancellationCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public int ActiveRequests => Volatile.Read(ref _activeRequests);
        public int MaximumActiveRequests => Volatile.Read(ref _maximumActiveRequests);
        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public void Release(int count) => _responses.Release(count);
        public void CompleteCancellation() => _completeCancellation.TrySetResult();
        public Task WaitForCancellationAsync() => _cancellationObserved.Task.WaitAsync(TestWait.Timeout);

        public async Task<Uri> WaitForReturnAsync()
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TestWait.Timeout);
            return await _returns.Reader.ReadAsync(timeout.Token);
        }

        public async Task WaitForStartsAsync(int count)
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TestWait.Timeout);
            for (int i = 0; i < count; i++)
                await _starts.Reader.ReadAsync(timeout.Token);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeRequests);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumActiveRequests);
                if (observed >= active)
                    break;
            }
            while (Interlocked.CompareExchange(ref _maximumActiveRequests, active, observed) != observed);
            Interlocked.Increment(ref _requestCount);
            _starts.Writer.TryWrite(true);

            try
            {
                await _responses.WaitAsync(cancellationToken);
                _returns.Writer.TryWrite(request.RequestUri!);
                return new HttpResponseMessage(statusCode) { Content = new ByteArrayContent(Payload) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                _cancellationObserved.TrySetResult();
                if (delayCancellationCompletion)
                    await _completeCancellation.Task;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _responses.Dispose();
            base.Dispose(disposing);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class DownloadFailureTests
{
    private const long UnthrottledBandwidth = 1L << 30;
    private static readonly byte[] Payload = [2, 4, 6, 8];

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailedMediaChangesTheThreadToErrorAndNotifiesObservers(HttpStatusCode statusCode)
    {
        using TempDirectory directory = new TempDirectory();
        GatedResponse response = new GatedResponse();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, cancellationToken) =>
            response.SendAsync(cancellationToken)));
        ProbeThread thread = CreateThread(board, totalMediaCount: 1);
        ConcurrentQueue<string> statuses = ObserveStatuses(thread);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, thread, directory, "failed");
        int removals = 0;
        using IDisposable removalRegistration = thread.RegisterRemovalSink(_ => Interlocked.Increment(ref removals));

        try
        {
            Assert.True(queue.TryEnqueue(item));
            await response.WaitForStartAsync();
            response.Complete(statusCode);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(ThreadActivity.Error, thread.Activity);
            await TestWait.UntilAsync(() => statuses.Contains("Error"),
                "The asynchronous state notification should expose the media failure.");
            KeyValuePair<string, string> failure = Assert.Single(thread.MediaFailures);
            Assert.Equal(item.MediaIdentifier, failure.Key);
            Assert.False(string.IsNullOrWhiteSpace(failure.Value));
            Assert.Equal(0, thread.DownloadedMediaCount);
            Assert.False(thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.False(File.Exists(item.DestinationPath));
            Assert.Equal(0, Volatile.Read(ref removals));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task AnotherMediumFinishingDoesNotHideAnUnresolvedMediaFailure()
    {
        using TempDirectory directory = new TempDirectory();
        GatedResponse failingResponse = new GatedResponse();
        GatedResponse successfulResponse = new GatedResponse();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((request, cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/media/failed.bin"
                ? failingResponse.SendAsync(cancellationToken)
                : successfulResponse.SendAsync(cancellationToken)));
        ProbeThread thread = CreateThread(board, totalMediaCount: 2);
        DownloadQueue queue = new DownloadQueue(2, UnthrottledBandwidth);
        DownloadItem failedItem = CreateItem(board, thread, directory, "failed");
        DownloadItem successfulItem = CreateItem(board, thread, directory, "successful");

        try
        {
            Assert.True(queue.TryEnqueue(failedItem));
            Assert.True(queue.TryEnqueue(successfulItem));
            await Task.WhenAll(failingResponse.WaitForStartAsync(), successfulResponse.WaitForStartAsync());

            failingResponse.Complete(HttpStatusCode.ServiceUnavailable);
            await TestWait.UntilAsync(() => !queue.IsInQueue(failedItem.DownloadUri.ToString()),
                "The failed medium should leave the queue while the other response remains gated.");
            Assert.Equal(ThreadActivity.Error, thread.Activity);
            Assert.Contains(failedItem.MediaIdentifier, thread.MediaFailures.Keys);
            Assert.True(queue.IsDownloading(successfulItem.DownloadUri.ToString()));
            Assert.Equal(0, thread.DownloadedMediaCount);

            successfulResponse.Complete(HttpStatusCode.OK);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(ThreadActivity.Error, thread.Activity);
            KeyValuePair<string, string> failure = Assert.Single(thread.MediaFailures);
            Assert.Equal(failedItem.MediaIdentifier, failure.Key);
            Assert.Equal(1, thread.DownloadedMediaCount);
            Assert.False(thread.IsMediaDownloaded(failedItem.MediaIdentifier));
            Assert.True(thread.IsMediaDownloaded(successfulItem.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(successfulItem.DestinationPath));
            Assert.False(File.Exists(failedItem.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedCancellationDoesNotReportAMediaError(bool stopEntireQueue)
    {
        using TempDirectory directory = new TempDirectory();
        GatedResponse response = new GatedResponse();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, cancellationToken) =>
            response.SendAsync(cancellationToken)));
        ProbeThread thread = CreateThread(board, totalMediaCount: 1);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, thread, directory, "cancelled");

        try
        {
            Assert.True(queue.TryEnqueue(item));
            await response.WaitForStartAsync();

            Task cancellation = stopEntireQueue
                ? queue.StopAllAsync()
                : queue.CancelDownloadsForThreadAsync(thread);
            await cancellation.WaitAsync(TestWait.Timeout);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(1, response.CancellationCount);
            Assert.NotEqual(ThreadActivity.Error, thread.Activity);
            Assert.Empty(thread.MediaFailures);
            Assert.Equal(0, thread.DownloadedMediaCount);
            Assert.False(thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.False(File.Exists(item.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private static ProbeThread CreateThread(StubImageBoard board, int totalMediaCount)
        => new ProbeThread(board.Board, new Uri("https://stub.test/g/thread/1"))
        {
            TotalMediaCount = totalMediaCount
        };

    private static DownloadItem CreateItem(StubImageBoard board, ProbeThread thread, TempDirectory directory,
        string mediaIdentifier)
        => new DownloadItem(new Uri($"https://stub.test/media/{mediaIdentifier}.bin"),
            Path.Combine(directory.Path, mediaIdentifier + ".bin"), board, thread, mediaIdentifier);

    private static ConcurrentQueue<string> ObserveStatuses(ProbeThread thread)
    {
        ConcurrentQueue<string> statuses = new ConcurrentQueue<string>();
        thread.PropertyChanged += (_, eventArgs) =>
        {
            if (string.IsNullOrEmpty(eventArgs.PropertyName) || eventArgs.PropertyName == nameof(thread.Status))
                statuses.Enqueue(thread.Status);
        };
        return statuses;
    }

    private sealed class GatedResponse
    {
        private readonly TaskCompletionSource _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpStatusCode> _response = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCount;

        public int CancellationCount => Volatile.Read(ref _cancellationCount);
        public Task WaitForStartAsync() => _started.Task.WaitAsync(TestWait.Timeout);
        public void Complete(HttpStatusCode statusCode) => _response.TrySetResult(statusCode);

        public async Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            try
            {
                HttpStatusCode statusCode = await _response.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(statusCode) { Content = new ByteArrayContent(Payload) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }
    }
}

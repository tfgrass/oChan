using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Storage;

namespace oChan.Tests;

public sealed class DownloadWorkerTests
{
    private static readonly byte[] Payload = [1, 2, 3, 5, 8, 13, 21, 34];

    [Fact]
    public async Task FailedBodyIsObservableAndTheNextAttemptDownloadsTheWholeFile()
    {
        using TempDirectory directory = new TempDirectory();
        int requests = 0;
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
        {
            int attempt = Interlocked.Increment(ref requests);
            HttpContent content = attempt == 1
                ? new StreamContent(new PartialFailureStream(Payload))
                : new ByteArrayContent(Payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore());

        await Assert.ThrowsAsync<IOException>(() => worker.ExecuteAsync(CancellationToken.None));

        Assert.False(File.Exists(item.DestinationPath));
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));

        await worker.ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, requests);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
    }

    [Fact]
    public async Task CancellationDuringBodyDoesNotPublishOrRemoveTheThread()
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PartialFailureStream(Payload, cancellation.Cancel))
            })));
        DownloadItem item = CreateItem(board, directory);
        int removals = 0;
        using IDisposable removalRegistration = item.Thread.RegisterRemovalSink(_ => Interlocked.Increment(ref removals));
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteAsync(cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref removals));
        Assert.False(File.Exists(item.DestinationPath));
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
    }

    [Fact]
    public async Task Media404IsAnObservableMediaFailureAndDoesNotRemoveTheThread()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        DownloadItem item = CreateItem(board, directory);
        int removals = 0;
        using IDisposable removalRegistration = item.Thread.RegisterRemovalSink(_ => Interlocked.Increment(ref removals));
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore());

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => worker.ExecuteAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(0, Volatile.Read(ref removals));
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task ManifestBackedRestartReusesMediaWithoutAnHttpRequest()
    {
        using TempDirectory directory = new TempDirectory();
        int requests = 0;
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }));
        DownloadItem first = CreateItem(board, directory, provideHash: false);
        await new DownloadWorker(first, new BandwidthLimiter(1L << 30), new MediaStore())
            .ExecuteAsync(CancellationToken.None);
        DownloadItem restarted = CreateItem(board, directory, provideHash: false);
        Assert.False(restarted.Thread.IsMediaDownloaded(restarted.MediaIdentifier));

        await new DownloadWorker(restarted, new BandwidthLimiter(1L << 30), new MediaStore())
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.True(restarted.Thread.IsMediaDownloaded(restarted.MediaIdentifier));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(restarted.DestinationPath));
    }

    [Fact]
    public async Task InvalidatingOneDestinationPreservesAnotherVerifiedCopyOfTheSameMedium()
    {
        using TempDirectory directory = new TempDirectory();
        HttpStatusCode responseStatus = HttpStatusCode.OK;
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(responseStatus) { Content = new ByteArrayContent(Payload) })));
        DownloadItem first = CreateItem(board, directory);
        DownloadItem second = new DownloadItem(first.DownloadUri,
            Path.Combine(directory.Path, "second", "456.bin"), board, first.Thread, first.MediaIdentifier,
            first.ExpectedSize, first.ExpectedMd5, first.OriginalFileName);
        MediaStore store = new MediaStore();
        DownloadQueue queue = new DownloadQueue(1, 1L << 30, store);
        try
        {
            Assert.True(queue.TryEnqueue(first));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(queue.TryEnqueue(second));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
            responseStatus = HttpStatusCode.ServiceUnavailable;
            File.Delete(first.DestinationPath);

            Assert.True(queue.TryEnqueue(first));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.True(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(second.DestinationPath));
            Assert.NotEmpty(first.Thread.MediaFailures);

            // Revalidating the healthy target must not erase the other target's failure.
            Assert.True(queue.TryEnqueue(second));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
            Assert.NotEmpty(first.Thread.MediaFailures);

            File.Delete(second.DestinationPath);
            Assert.True(queue.TryEnqueue(second));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.False(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ASizeMismatchDoesNotMarkTheMediumCompleted()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })));
        DownloadItem item = CreateItem(board, directory, Payload.Length + 1);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore());

        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ExecuteAsync(CancellationToken.None));

        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task FailedManifestCommitDoesNotMarkTheMediumCompletedEvenWhenFileWasPublished()
    {
        using TempDirectory directory = new TempDirectory();
        int requests = 0;
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }));
        DownloadItem item = CreateItem(board, directory);
        MediaStore failingStore = new MediaStore((source, destination, overwrite) =>
        {
            if (Path.GetFileName(destination) == ".ochan-manifest.json")
                throw new IOException("Synthetic manifest publication failure.");
            File.Move(source, destination, overwrite);
        });
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), failingStore);

        await Assert.ThrowsAsync<IOException>(() => worker.ExecuteAsync(CancellationToken.None));

        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));

        await new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore())
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContradictoryOrTruncatedHttpContentLengthCannotProduceSuccess(bool hasProviderSize)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
        {
            ByteArrayContent content = new ByteArrayContent(Payload);
            content.Headers.ContentLength = Payload.Length + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/123"));
        DownloadItem item = new DownloadItem(new Uri("https://stub.test/media/456.bin"),
            Path.Combine(directory.Path, "456.bin"), board, thread, "456",
            expectedSize: hasProviderSize ? Payload.Length : null);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore());

        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ExecuteAsync(CancellationToken.None));

        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task UnexpectedPartialResponseCannotBecomeACompletedMedium()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Payload[..3])
            })));
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/123"));
        DownloadItem item = new DownloadItem(new Uri("https://stub.test/media/456.bin"),
            Path.Combine(directory.Path, "456.bin"), board, thread, "456");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DownloadWorker(item, new BandwidthLimiter(1L << 30)).ExecuteAsync(CancellationToken.None));

        Assert.False(thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyStallTimesOutEvenWhenTheStreamIgnoresCancellation(bool ignoreCancellation)
    {
        using TempDirectory directory = new TempDirectory();
        ManualWorkerTime clock = new ManualWorkerTime();
        StalledBodyStream body = new StalledBodyStream(ignoreCancellation, emitPrefix: true);
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);

        Task execution = worker.ExecuteAsync(CancellationToken.None);
        await body.Stalled.Task.WaitAsync(TestWait.Timeout);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(execution.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TestWait.Timeout));
        Assert.True(body.WasDisposed);
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task CallerCancellationDuringAStalledReadRemainsCancellation()
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ManualWorkerTime clock = new ManualWorkerTime();
        StalledBodyStream body = new StalledBodyStream(ignoreCancellation: true);
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);

        Task execution = worker.ExecuteAsync(cancellation.Token);
        await body.Stalled.Task.WaitAsync(TestWait.Timeout);
        cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TestWait.Timeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(body.WasDisposed);
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.False(File.Exists(item.DestinationPath));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedBodyCreationIsBoundedAndItsLateStreamIsDisposed(bool cancelCaller)
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ManualWorkerTime clock = new ManualWorkerTime();
        DelayedBodyContent content = new DelayedBodyContent();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);

        Task execution = worker.ExecuteAsync(cancellation.Token);
        await content.Started.Task.WaitAsync(TestWait.Timeout);
        if (cancelCaller)
        {
            cancellation.Cancel();
            Assert.False(execution.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TestWait.Timeout));
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TestWait.Timeout));
        }
        TrackingBodyStream lateBody = new TrackingBodyStream();
        content.Complete(lateBody);
        await TestWait.UntilAsync(() => lateBody.WasDisposed, "A body arriving after its deadline was not disposed.");

        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.False(File.Exists(item.DestinationPath));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task CallerCancellationWaitsForTheNetworkReadToFinishItsCleanup()
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ManualWorkerTime clock = new ManualWorkerTime();
        DrainingBodyStream body = new DrainingBodyStream();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);

        Task execution = worker.ExecuteAsync(cancellation.Token);
        await body.Started.Task.WaitAsync(TestWait.Timeout);
        cancellation.Cancel();
        await body.CancellationSeen.Task.WaitAsync(TestWait.Timeout);
        Assert.False(execution.IsCompleted);
        Assert.False(body.WasDisposed);
        body.AllowCleanup.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TestWait.Timeout));

        Assert.True(body.WasDisposed);
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task EachSuccessfulNetworkReadStartsAFreshIdleAllowance()
    {
        using TempDirectory directory = new TempDirectory();
        ManualWorkerTime clock = new ManualWorkerTime();
        GatedBodyStream body = new GatedBodyStream();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1L << 30), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);

        Task execution = worker.ExecuteAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => body.Reads == 1);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        body.Supply(Payload[..3]);
        await TestWait.UntilAsync(() => body.Reads == 2);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert.False(execution.IsCompleted);
        body.Supply(Payload[3..]);
        body.Complete();
        await execution.WaitAsync(TestWait.Timeout);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task SlowBandwidthAdmissionDoesNotConsumeTheNetworkIdleDeadline()
    {
        using TempDirectory directory = new TempDirectory();
        ManualWorkerTime clock = new ManualWorkerTime();
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })));
        DownloadItem item = CreateItem(board, directory);
        DownloadWorker worker = new DownloadWorker(item, new BandwidthLimiter(1, clock), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);

        Task execution = worker.ExecuteAsync(CancellationToken.None);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(Payload.Length));
        clock.Advance(TimeSpan.FromSeconds(Payload.Length - 1));
        Assert.False(execution.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await execution.WaitAsync(TestWait.Timeout);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task BodyTimeoutReleasesTheOnlyQueueSlotForTheNextMedium()
    {
        using TempDirectory directory = new TempDirectory();
        ManualWorkerTime clock = new ManualWorkerTime();
        StalledBodyStream stalled = new StalledBodyStream(ignoreCancellation: true);
        TaskCompletionSource nextRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using StubImageBoard board = new StubImageBoard(new DelegateHandler((request, _) =>
        {
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith("stall.bin", StringComparison.Ordinal))
                content = new StreamContent(stalled);
            else
            {
                nextRequested.TrySetResult();
                content = new ByteArrayContent(Payload);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        IThread firstThread = board.GetThread(new Uri("https://stub.test/g/thread/1"));
        IThread secondThread = board.GetThread(new Uri("https://stub.test/g/thread/2"));
        DownloadItem first = new DownloadItem(new Uri("https://stub.test/media/stall.bin"),
            Path.Combine(directory.Path, "1", "stall.bin"), board, firstThread, "stall");
        DownloadItem second = new DownloadItem(new Uri("https://stub.test/media/next.bin"),
            Path.Combine(directory.Path, "2", "next.bin"), board, secondThread, "next", Payload.Length);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30, bodyIdleTimeout: TimeSpan.FromSeconds(1), timeProvider: clock);
        try
        {
            Assert.True(queue.TryEnqueue(first));
            Assert.True(queue.TryEnqueue(second));
            await stalled.Stalled.Task.WaitAsync(TestWait.Timeout);
            Assert.False(nextRequested.Task.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            await nextRequested.Task.WaitAsync(TestWait.Timeout);
            await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.True(stalled.WasDisposed);
            Assert.False(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
            Assert.True(second.Thread.IsMediaDownloaded(second.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(second.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public void TimeoutWrapperBorrowsRatherThanDisposesItsUnderlyingStream()
    {
        using TrackingBodyStream body = new TrackingBodyStream();
        using (IdleTimeoutReadStream wrapper = new IdleTimeoutReadStream(body, TimeSpan.FromSeconds(1), TimeProvider.System))
        {
            Assert.True(wrapper.CanRead);
        }

        Assert.False(body.WasDisposed);
        Assert.Equal(Payload[0], body.ReadByte());
    }

    private static DownloadItem CreateItem(StubImageBoard board, TempDirectory directory, long? expectedSize = null,
        bool provideHash = true)
    {
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/123"));
        return new DownloadItem(new Uri("https://stub.test/media/456.bin"),
            Path.Combine(directory.Path, "stub", "g", "123", "456.bin"), board, thread, "456",
            expectedSize: expectedSize ?? Payload.Length,
            expectedMd5: provideHash ? Convert.ToBase64String(MD5.HashData(Payload)) : null,
            originalFileName: "same-name.bin");
    }

    private sealed class StalledBodyStream(bool ignoreCancellation, bool emitPrefix = false) : MemoryStream(Payload, writable: false)
    {
        private readonly TaskCompletionSource<int> _pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _emittedPrefix;
        public TaskCompletionSource Stalled { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (emitPrefix && !_emittedPrefix)
            {
                _emittedPrefix = true;
                return base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
            }
            Stalled.TrySetResult();
            return new ValueTask<int>(ignoreCancellation ? _pending.Task : _pending.Task.WaitAsync(cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            _pending.TrySetCanceled();
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingBodyStream() : MemoryStream(Payload, writable: false)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class DrainingBodyStream() : MemoryStream(Payload, writable: false)
    {
        public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationSeen { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationSeen.TrySetResult();
                await AllowCleanup.Task;
                throw;
            }
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class DelayedBodyContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _body = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(Stream stream) => _body.TrySetResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _body.Task;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = Payload.Length; return true; }
    }

    private sealed class GatedBodyStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public void Supply(byte[] chunk) => _chunks.Writer.TryWrite(chunk);
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            byte[] chunk = await _chunks.Reader.ReadAsync(cancellationToken);
            chunk.AsMemory().CopyTo(buffer);
            return chunk.Length;
        }
        protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualWorkerTime : TimeProvider
    {
        private readonly object _sync = new object();
        private readonly List<ManualTimer> _timers = new List<ManualTimer>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public int PendingTimers { get { lock (_sync) { return _timers.Count; } } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.ChangeLocked(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToList();
                foreach (ManualTimer timer in due)
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + timer.Period.Ticks;
            }
            foreach (ManualTimer timer in due) timer.Callback(timer.State);
        }

        public Task WaitForTimerAtAsync(TimeSpan elapsed) => TestWait.UntilAsync(() =>
        {
            lock (_sync) { return _timers.Any(timer => timer.Due == elapsed.Ticks); }
        }, $"No worker timer was scheduled for {elapsed}.");

        private sealed class ManualTimer(ManualWorkerTime owner, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    ChangeLocked(dueTime, period);
                    return true;
                }
            }
            public void ChangeLocked(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period;
            }
            public void Dispose()
            {
                lock (owner._sync) { _disposed = true; owner._timers.Remove(this); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

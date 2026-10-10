using System.Net;
using System.Text;
using oChan.Boards.Base;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class AdapterCancellationTests
{
    public static IEnumerable<object[]> CancellationCases()
    {
        foreach (bool eightKun in new[] { false, true })
        foreach (bool boardWatch in new[] { false, true })
        foreach (bool metadata in new[] { false, true })
        foreach (bool responseBody in new[] { false, true })
        foreach (bool stopSession in new[] { false, true })
            yield return new object[] { eightKun, boardWatch, metadata, responseBody, stopSession };
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task RemoveAndStopCancelRealProviderIoAndAwaitItsCompletion(
        bool eightKun, bool boardWatch, bool metadata, bool responseBody, bool stopSession)
    {
        using TempDirectory directory = new TempDirectory();
        string blockedPath = metadata ? "/boards.json" : boardWatch ? "/g/catalog.json" : ThreadPath(eightKun, 123);
        using CancellationGate gate = new CancellationGate();
        using CancellationHandler handler = new CancellationHandler(blockedPath, responseBody, gate);
        using HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        IImageBoard provider = Provider(eightKun, client);
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024);
        await using Registry registry = NewRegistry(provider, queue, directory.Path);

        try
        {
            AdmissionResult admission = await registry.AddAsync(boardWatch ? BoardUrl(eightKun) : ThreadUrl(eightKun, 123));
            Assert.Equal(AdmissionStatus.Added, admission.Status);
            Assert.Equal(boardWatch ? 0 : 1, registry.Threads.Count);
            Assert.Equal(boardWatch ? 1 : 0, registry.Boards.Count);
            await gate.Entered.WaitAsync(TestWait.Timeout);

            Task stopping = stopSession ? registry.StopAsync() : boardWatch
                ? registry.RemoveBoardAsync(admission.Board!)
                : registry.RemoveThreadAsync(admission.Thread!);
            await gate.Cancelled.WaitAsync(TestWait.Timeout);
            Assert.False(gate.Finished.IsCompleted);
            Assert.False(stopping.IsCompleted);

            gate.CompleteCancellation();
            await stopping.WaitAsync(TestWait.Timeout);
            Assert.True(gate.Finished.IsCompletedSuccessfully);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Empty(registry.Boards);
            Assert.Empty(registry.Threads);
            Assert.Equal(1, handler.MetadataRequests);
            Assert.Equal(!metadata && boardWatch ? 1 : 0, handler.CatalogRequests);
            Assert.Equal(!metadata && !boardWatch ? 1 : 0, handler.ThreadRequests);
            Assert.Equal(0, handler.MediaRequests);
            Assert.Equal(1, handler.BlockedRequests);
            Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
            if (admission.Thread != null)
            {
                BaseThread thread = Assert.IsAssignableFrom<BaseThread>(admission.Thread);
                Assert.Equal(0, thread.TotalMediaCount);
                Assert.Equal(0, thread.DownloadedMediaCount);
                Assert.NotEqual("Error", thread.Status);
            }
            // Repeated shutdown must also observe the already-drained watch.
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
        }
        finally
        {
            gate.AbortForCleanup();
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemovingOneThreadLeavesAnotherRealThreadAndItsDownloadUsable(bool eightKun, bool responseBody)
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationGate gate = new CancellationGate();
        using CancellationHandler handler = new CancellationHandler(ThreadPath(eightKun, 123), responseBody, gate);
        using HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        IImageBoard provider = Provider(eightKun, client);
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024);
        await using Registry registry = NewRegistry(provider, queue, directory.Path);

        try
        {
            AdmissionResult first = await registry.AddAsync(ThreadUrl(eightKun, 123));
            await gate.Entered.WaitAsync(TestWait.Timeout);
            Task removingFirst = registry.RemoveThreadAsync(first.Thread!);
            await gate.Cancelled.WaitAsync(TestWait.Timeout);

            AdmissionResult second = await registry.AddAsync(ThreadUrl(eightKun, 456));
            Assert.Equal(AdmissionStatus.Added, second.Status);
            BaseThread secondThread = Assert.IsAssignableFrom<BaseThread>(second.Thread);
            await TestWait.UntilAsync(() => secondThread.DownloadedMediaCount == 1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.False(removingFirst.IsCompleted);
            Assert.Same(second.Thread, Assert.Single(registry.Threads));
            Assert.Equal("Finished", secondThread.Status);
            Assert.Equal(1, handler.MediaRequests);
            Assert.Equal(1, handler.MetadataRequests);

            gate.CompleteCancellation();
            await removingFirst.WaitAsync(TestWait.Timeout);
            Assert.Same(second.Thread, Assert.Single(registry.Threads));
            Assert.Empty(first.Thread!.DownloadedMedia);
            Assert.Equal(1, secondThread.DownloadedMediaCount);
        }
        finally
        {
            gate.AbortForCleanup();
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateReadmissionWaitsForRealProviderDrainAndRejectsStaleRemoval(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationGate gate = new CancellationGate();
        using CancellationHandler handler = new CancellationHandler(ThreadPath(eightKun, 123), false, gate, blockOnlyFirst: true);
        using HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        IImageBoard provider = Provider(eightKun, client);
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024);
        await using Registry registry = NewRegistry(provider, queue, directory.Path);
        string url = ThreadUrl(eightKun, 123);

        try
        {
            AdmissionResult original = await registry.AddAsync(url);
            await gate.Entered.WaitAsync(TestWait.Timeout);
            Task removal = registry.RemoveThreadAsync(original.Thread!);
            await gate.Cancelled.WaitAsync(TestWait.Timeout);
            Task<AdmissionResult> readmission = registry.AddAsync(url);
            Assert.False(removal.IsCompleted);
            Assert.False(readmission.IsCompleted);
            Assert.Empty(registry.Threads);
            Assert.Equal(1, handler.ThreadRequests);

            gate.CompleteCancellation();
            await removal.WaitAsync(TestWait.Timeout);
            AdmissionResult replacement = await readmission.WaitAsync(TestWait.Timeout);
            Assert.Equal(AdmissionStatus.Added, replacement.Status);
            Assert.NotSame(original.Thread, replacement.Thread);
            BaseThread replacementThread = Assert.IsAssignableFrom<BaseThread>(replacement.Thread);
            await TestWait.UntilAsync(() => replacementThread.DownloadedMediaCount == 1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            await registry.RemoveThreadAsync(original.Thread!).WaitAsync(TestWait.Timeout);
            original.Thread!.NotifyThreadRemoval(false);
            Assert.Same(replacement.Thread, Assert.Single(registry.Threads));
            AdmissionResult duplicate = await registry.AddAsync(url);
            Assert.Equal(AdmissionStatus.AlreadyPresent, duplicate.Status);
            Assert.Same(replacement.Thread, duplicate.Thread);
            Assert.Equal(2, handler.ThreadRequests);
            Assert.Equal(1, handler.MetadataRequests);
            Assert.Equal(1, handler.MediaRequests);
            Assert.Equal(1, handler.BlockedRequests);
            Assert.Empty(original.Thread.DownloadedMedia);
            Assert.Equal("Finished", replacementThread.Status);
        }
        finally
        {
            gate.AbortForCleanup();
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingMetadataWaiterDoesNotCancelTheOtherThreadsSharedMetadataRequest(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationGate gate = new CancellationGate();
        using CancellationHandler handler = new CancellationHandler("/boards.json", false, gate);
        using HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        IImageBoard provider = eightKun ? new ObservedEightKunProvider(client) : new ObservedFourChanProvider(client);
        await using Registry registry = NewRegistry(provider, new DownloadQueue(2, 1024 * 1024), directory.Path);

        try
        {
            AdmissionResult first = await registry.AddAsync(ThreadUrl(eightKun, 123));
            await gate.Entered.WaitAsync(TestWait.Timeout);
            AdmissionResult waiting = await registry.AddAsync(ThreadUrl(eightKun, 456));
            IObservedCheck observed = Assert.IsAssignableFrom<IObservedCheck>(waiting.Thread);
            Task pendingCheck = await observed.InitialCheck.WaitAsync(TestWait.Timeout);
            Assert.False(pendingCheck.IsCompleted);

            await registry.RemoveThreadAsync(waiting.Thread!).WaitAsync(TestWait.Timeout);
            Assert.False(gate.Cancelled.IsCompleted);
            Assert.False(gate.Finished.IsCompleted);
            Assert.Equal(1, handler.MetadataRequests);
            Assert.Equal(0, handler.ThreadRequests);
            Assert.Same(first.Thread, Assert.Single(registry.Threads));
            Assert.NotEqual("Error", waiting.Thread!.Status);

            Task stopping = registry.StopAsync();
            await gate.Cancelled.WaitAsync(TestWait.Timeout);
            Assert.False(stopping.IsCompleted);
            gate.CompleteCancellation();
            await stopping.WaitAsync(TestWait.Timeout);
        }
        finally
        {
            gate.AbortForCleanup();
            await registry.StopAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private static Registry NewRegistry(IImageBoard provider, DownloadQueue queue, string outputDirectory)
        => new Registry([provider], queue, outputDirectory, TimeSpan.FromHours(1), 3600);

    private static IImageBoard Provider(bool eightKun, HttpClient client)
        => eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);

    private static string BoardUrl(bool eightKun)
        => eightKun ? "https://8kun.top/g/catalog.html" : "https://boards.4chan.org/g/catalog";

    private static string ThreadUrl(bool eightKun, int id)
        => eightKun ? $"https://8kun.top/g/res/{id}.html" : $"https://boards.4chan.org/g/thread/{id}";

    private static string ThreadPath(bool eightKun, int id)
        => eightKun ? $"/g/res/{id}.json" : $"/g/thread/{id}.json";

    // Observe the production async method after it has reached its first pending
    // await. With boards.json held by the first watch, the second is now waiting
    // inside the shared metadata gate, rather than merely reporting "Checking".
    private interface IObservedCheck
    {
        Task<Task> InitialCheck { get; }
    }

    private sealed class ObservedFourChanProvider(HttpClient client) : FourChanImageBoard(client)
    {
        public override IThread GetThread(Uri uri)
        {
            IThread passive = base.GetThread(uri);
            return new ObservedFourChanThread(passive.Board, passive.ThreadUri);
        }
    }

    private sealed class ObservedEightKunProvider(HttpClient client) : EightKunImageBoard(client)
    {
        public override IThread GetThread(Uri uri)
        {
            IThread passive = base.GetThread(uri);
            return new ObservedEightKunThread(passive.Board, passive.ThreadUri);
        }
    }

    private sealed class ObservedFourChanThread(IBoard board, Uri uri) : FourChanThread(board, uri), IObservedCheck
    {
        private readonly TaskCompletionSource<Task> _initialCheck = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Task> InitialCheck => _initialCheck.Task;
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            Task check = base.checkThreadAsync(queue, cancellationToken);
            _initialCheck.TrySetResult(check);
            return check;
        }
    }

    private sealed class ObservedEightKunThread(IBoard board, Uri uri) : EightKunThread(board, uri), IObservedCheck
    {
        private readonly TaskCompletionSource<Task> _initialCheck = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Task> InitialCheck => _initialCheck.Task;
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            Task check = base.checkThreadAsync(queue, cancellationToken);
            _initialCheck.TrySetResult(check);
            return check;
        }
    }

    /// <summary>Actual transport cancellation and completion are separate, bounded steps.</summary>
    private sealed class CancellationGate : IDisposable
    {
        private readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public Task Cancelled => _cancelled.Task;
        public Task Finished => _finished.Task;
        public void CompleteCancellation() => _complete.TrySetResult();

        public async Task BlockAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _cancelled.TrySetResult());
            _entered.TrySetResult();
            try
            {
                Task wakeup = await Task.WhenAny(_cancelled.Task, _abort.Task).WaitAsync(TestWait.Timeout);
                if (ReferenceEquals(wakeup, _abort.Task))
                    throw new OperationCanceledException("Synthetic gate cleanup.", new CancellationToken(true));
                await _complete.Task.WaitAsync(TestWait.Timeout);
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation signal did not carry a cancelled token.");
            }
            finally { _finished.TrySetResult(); }
        }

        public void AbortForCleanup()
        {
            _abort.TrySetResult();
            _complete.TrySetResult();
        }

        public void Dispose() => AbortForCleanup();
    }

    private sealed class CancellationHandler(string blockedPath, bool responseBody, CancellationGate gate, bool blockOnlyFirst = false) : HttpMessageHandler
    {
        private int _metadataRequests;
        private int _catalogRequests;
        private int _threadRequests;
        private int _mediaRequests;
        private int _blockedRequests;
        private int _matchingRequests;
        public int MetadataRequests => Volatile.Read(ref _metadataRequests);
        public int CatalogRequests => Volatile.Read(ref _catalogRequests);
        public int ThreadRequests => Volatile.Read(ref _threadRequests);
        public int MediaRequests => Volatile.Read(ref _mediaRequests);
        public int BlockedRequests => Volatile.Read(ref _blockedRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            string path = uri.AbsolutePath;
            string payload;
            if (path == "/boards.json")
            {
                Interlocked.Increment(ref _metadataRequests);
                payload = uri.Host == "8kun.top"
                    ? "[{\"uri\":\"g\",\"title\":\"Synthetic\"}]"
                    : "{\"boards\":[{\"board\":\"g\",\"title\":\"Synthetic\"}]}";
            }
            else if (path == "/g/catalog.json")
            {
                Interlocked.Increment(ref _catalogRequests);
                payload = "[{\"page\":1,\"threads\":[{\"no\":123,\"sub\":\"Late discovery\"}]}]";
            }
            else if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _threadRequests);
                payload = "{\"posts\":[{\"no\":123,\"sub\":\"Synthetic media\",\"tim\":123000,\"ext\":\".jpg\",\"filename\":\"synthetic\",\"fsize\":3}]}";
            }
            else
            {
                Interlocked.Increment(ref _mediaRequests);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }

            if (path == blockedPath && (!blockOnlyFirst || Interlocked.Increment(ref _matchingRequests) == 1))
            {
                Interlocked.Increment(ref _blockedRequests);
                if (responseBody)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockedJsonContent(payload, gate) };
                await gate.BlockAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class BlockedJsonContent(string payload, CancellationGate gate) : HttpContent
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(payload);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new BlockedJsonReadStream(_payload, gate));
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using Stream body = new BlockedJsonReadStream(_payload, gate);
            await body.CopyToAsync(stream, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    private sealed class BlockedJsonReadStream(byte[] payload, CancellationGate gate) : Stream
    {
        private int _prefixPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            int prefixLength = Math.Min(5, payload.Length);
            if (_prefixPosition < prefixLength)
            {
                int count = Math.Min(buffer.Length, prefixLength - _prefixPosition);
                payload.AsMemory(_prefixPosition, count).CopyTo(buffer);
                _prefixPosition += count;
                return count;
            }

            // The HTTP content exposes a stream immediately. Cancellation is
            // observed by the actual pending body read after a JSON prefix.
            await gate.BlockAsync(cancellationToken);
            throw new InvalidOperationException("The cancelled body read must not complete successfully.");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

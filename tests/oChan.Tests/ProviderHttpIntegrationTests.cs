using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Http;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class ProviderHttpIntegrationTests
{
    private static readonly DateTimeOffset Modified = new DateTimeOffset(2026, 10, 10, 10, 20, 30, TimeSpan.Zero);
    private const string EmptyThread = "{\"posts\":[{\"no\":123,\"sub\":\"Synthetic thread\"}]}";
    private const string OneMedium = "{\"posts\":[{\"no\":123,\"sub\":\"Synthetic thread\",\"tim\":1000,\"ext\":\".jpg\",\"filename\":\"sample\",\"fsize\":4}]}";
    private const string TwoMedia = "{\"posts\":[{\"no\":123,\"sub\":\"Final snapshot\",\"tim\":1000,\"ext\":\".jpg\",\"filename\":\"sample\",\"fsize\":4},{\"no\":124,\"tim\":1001,\"ext\":\".jpg\",\"filename\":\"sample\",\"fsize\":4}]}";

    [Fact]
    public void DefaultProvidersUseTheirSharedPolicyClient()
    {
        Assert.Same(ProviderHttpClientFactory.GetDefaultClient("4chan"), new FourChanImageBoard().GetHttpClient());
        Assert.Same(new FourChanImageBoard().GetHttpClient(), new FourChanImageBoard().GetHttpClient());
        Assert.Same(ProviderHttpClientFactory.GetDefaultClient("8kun"), new EightKunImageBoard().GetHttpClient());
        Assert.Same(new EightKunImageBoard().GetHttpClient(), new EightKunImageBoard().GetHttpClient());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotModifiedUsesLastModifiedAndRetriesKnownFailedMediaWithoutReadingItsBody(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        using UnreadableContent unchangedBody = new UnreadableContent();
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = (count, _, _) => Task.FromResult(count == 1
                ? Json(OneMedium, Modified)
                : new HttpResponseMessage(HttpStatusCode.NotModified) { Content = unchangedBody }),
            MediaReply = (count, _) => Task.FromResult(count == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Media())
        };
        using HttpClient client = new HttpClient(handler);
        IImageBoard provider = Provider(eightKun, client);
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = NewRegistry(provider, queue, directory.Path);
        IThread thread = (await registry.AddAsync(ThreadUrl(eightKun))).Thread!;
        IManualThread observed = Assert.IsAssignableFrom<IManualThread>(thread);
        await TestWait.UntilAsync(() => observed.Clock.Waits == 1);
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(1, handler.MediaRequests);
        Assert.Empty(thread.DownloadedMedia);
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);

        observed.Clock.Advance();
        await TestWait.UntilAsync(() => observed.Clock.Waits == 2);
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

        Assert.Equal(new DateTimeOffset?[] { null, Modified }, handler.Conditions.ToArray());
        Assert.Equal(0, unchangedBody.ReadAttempts);
        Assert.True(unchangedBody.Disposed);
        Assert.Equal(2, handler.MediaRequests);
        Assert.Equal("1000", Assert.Single(thread.DownloadedMedia));
        Assert.Equal(1, thread.TotalMediaCount);
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
        Assert.Same(thread, Assert.Single(registry.Threads));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory.Path, "*.jpg", SearchOption.AllDirectories))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotModifiedWithoutAnAcceptedSnapshotIsAnError(bool eightKun)
    {
        using UnreadableContent body = new UnreadableContent();
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified) { Content = body })
        };
        using HttpClient client = new HttpClient(handler);
        IThread thread = Provider(eightKun, client).GetThread(new Uri(ThreadUrl(eightKun)));

        await Assert.ThrowsAsync<InvalidDataException>(() => thread.checkThreadAsync(new DownloadQueue(1, 1024 * 1024)));

        Assert.Equal(0, body.ReadAttempts);
        Assert.True(body.Disposed);
        Assert.Null(Assert.Single(handler.Conditions));
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
        Assert.Equal(0, thread.TotalMediaCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidJsonDoesNotCommitItsLastModifiedValidator(bool eightKun)
    {
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = (count, _, _) => Task.FromResult(Json(count == 1 ? "{ invalid" : EmptyThread, Modified))
        };
        using HttpClient client = new HttpClient(handler);
        IThread thread = Provider(eightKun, client).GetThread(new Uri(ThreadUrl(eightKun)));
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);

        await Assert.ThrowsAnyAsync<JsonException>(() => thread.checkThreadAsync(queue));
        await thread.checkThreadAsync(queue);

        Assert.Equal(new DateTimeOffset?[] { null, null }, handler.Conditions.ToArray());
        Assert.Equal("Synthetic thread", thread.Title);
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
    }

    [Fact]
    public async Task ArchivedFinalSnapshotAddsItsMediaAndStopsPollingWithoutRemovingOrCancellingDownloads()
    {
        using TempDirectory directory = new TempDirectory();
        TaskCompletionSource releaseMedia = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource mediaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = (count, _, _) => Task.FromResult(Json(count == 1 ? OneMedium : TwoMedia.Replace("\"no\":123", "\"no\":123,\"archived\":1", StringComparison.Ordinal))),
            MediaReply = async (count, token) =>
            {
                if (count == 1)
                {
                    mediaEntered.TrySetResult();
                    await releaseMedia.Task.WaitAsync(token);
                }
                return Media();
            }
        };
        using HttpClient client = new HttpClient(handler);
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = NewRegistry(Provider(false, client), queue, directory.Path);
        try
        {
            IThread thread = (await registry.AddAsync(ThreadUrl(false))).Thread!;
            IManualThread observed = Assert.IsAssignableFrom<IManualThread>(thread);
            await TestWait.UntilAsync(() => observed.Clock.Waits == 1);
            await mediaEntered.Task.WaitAsync(TestWait.Timeout);
            observed.Clock.Advance();
            await TestWait.UntilAsync(() => observed.CompletedChecks == 2);

            Assert.Equal(ThreadSourceState.Archived, thread.SourceState);
            Assert.Equal("Final snapshot", thread.Title);
            Assert.Equal(2, thread.TotalMediaCount);
            Assert.Equal(1, handler.MediaRequests);
            Assert.False(queue.WaitForIdleAsync().IsCompleted);
            Assert.Same(thread, Assert.Single(registry.Threads));
            Assert.Equal(0, observed.RemovalRequests);

            releaseMedia.TrySetResult();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, thread.DownloadedMediaCount);
            Assert.Equal(2, handler.MediaRequests);
            Assert.Equal(ThreadSourceState.Archived, thread.SourceState);
            await thread.checkThreadAsync(queue);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, handler.ThreadRequests);
            Assert.Equal(1, observed.Clock.Waits);
            Assert.Same(thread, Assert.Single(registry.Threads));
            Assert.Equal(0, observed.RemovalRequests);
        }
        finally { releaseMedia.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingThreadStopsPollingAndKeepsActiveAndQueuedMediaDraining(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        TaskCompletionSource releaseMedia = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource mediaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = (count, _, _) => Task.FromResult(count == 1 ? Json(TwoMedia) : new HttpResponseMessage(HttpStatusCode.NotFound)),
            MediaReply = async (count, token) =>
            {
                if (count == 1)
                {
                    mediaEntered.TrySetResult();
                    await releaseMedia.Task.WaitAsync(token);
                }
                return Media();
            }
        };
        using HttpClient client = new HttpClient(handler);
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = NewRegistry(Provider(eightKun, client), queue, directory.Path);
        try
        {
            IThread thread = (await registry.AddAsync(ThreadUrl(eightKun))).Thread!;
            IManualThread observed = Assert.IsAssignableFrom<IManualThread>(thread);
            await TestWait.UntilAsync(() => observed.Clock.Waits == 1);
            await mediaEntered.Task.WaitAsync(TestWait.Timeout);
            observed.Clock.Advance();
            await TestWait.UntilAsync(() => observed.CompletedChecks == 2);

            Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
            Assert.Equal(2, thread.TotalMediaCount);
            Assert.Equal(1, handler.MediaRequests);
            Assert.False(queue.WaitForIdleAsync().IsCompleted);
            Assert.Same(thread, Assert.Single(registry.Threads));
            Assert.Equal(0, observed.RemovalRequests);
            releaseMedia.TrySetResult();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(2, thread.DownloadedMediaCount);
            Assert.Equal(2, handler.MediaRequests);
            Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
            await thread.checkThreadAsync(queue);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, handler.ThreadRequests);
            Assert.Equal(1, observed.Clock.Waits);
            Assert.Same(thread, Assert.Single(registry.Threads));
            Assert.Equal(0, observed.RemovalRequests);
        }
        finally { releaseMedia.TrySetResult(); }
    }

    [Fact]
    public async Task LockedEightKunThreadRemainsActiveAndPollsAgain()
    {
        using TempDirectory directory = new TempDirectory();
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = (_, _, _) => Task.FromResult(Json("{\"posts\":[{\"no\":123,\"locked\":1,\"sub\":\"Locked\"}]}"))
        };
        using HttpClient client = new HttpClient(handler);
        await using Registry registry = NewRegistry(Provider(true, client), new DownloadQueue(1, 1024 * 1024), directory.Path);
        IThread thread = (await registry.AddAsync(ThreadUrl(true))).Thread!;
        IManualThread observed = Assert.IsAssignableFrom<IManualThread>(thread);
        await TestWait.UntilAsync(() => observed.Clock.Waits == 1);
        observed.Clock.Advance();
        await TestWait.UntilAsync(() => observed.Clock.Waits == 2);

        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
        Assert.Equal(2, handler.ThreadRequests);
        Assert.Same(thread, Assert.Single(registry.Threads));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task JsonBodyDeadlineAndCallerCancellationReachTheRealProviderRead(bool eightKun, bool catalog, bool cancel)
    {
        ManualTime clock = new ManualTime();
        CancellationGate gate = new CancellationGate();
        using BlockedContent body = new BlockedContent(gate);
        using FixtureHandler handler = new FixtureHandler { ReportSending = true };
        if (catalog) handler.CatalogReply = () => WithBody(body);
        else handler.ThreadReply = (_, _, _) => Task.FromResult(WithBody(body));
        using HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        JsonResponseReader reader = new JsonResponseReader(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), clock);
        IImageBoard provider = Provider(eightKun, client, reader);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        IBoard? board = catalog ? provider.GetBoard(new Uri(BoardUrl(eightKun))) : null;
        IThread? thread = catalog ? null : provider.GetThread(new Uri(ThreadUrl(eightKun)));
        Task pending = board != null
            ? board.GetThreadsAsync(cancellation.Token)
            : thread!.checkThreadAsync(new DownloadQueue(1, 1024 * 1024), cancellation.Token);
        await gate.Entered.Task.WaitAsync(TestWait.Timeout);
        RequestPolicyProgress? progress = board != null ? board.RequestProgress : thread!.RequestProgress;
        Assert.Equal(RequestPolicyPhase.Sending, Assert.IsType<RequestPolicyProgress>(progress).Phase);

        if (cancel) cancellation.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(5));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestWait.Timeout));
        else await Assert.ThrowsAsync<TimeoutException>(() => pending).WaitAsync(TestWait.Timeout);

        await gate.Finished.Task.WaitAsync(TestWait.Timeout);
        Assert.True(gate.Finished.Task.IsCompletedSuccessfully);
        Assert.True(body.Disposed);
        Assert.Null(board != null ? board.RequestProgress : thread!.RequestProgress);
        Assert.Equal(catalog ? 0 : 1, handler.ThreadRequests);
        Assert.Equal(catalog ? 1 : 0, handler.CatalogRequests);
        Assert.Equal(0, handler.MediaRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionalMetadataBodyTimeoutFallsBackAndCanBeRetried(bool eightKun, bool catalog)
    {
        ManualTime clock = new ManualTime();
        CancellationGate gate = new CancellationGate();
        using BlockedContent body = new BlockedContent(gate);
        using FixtureHandler handler = new FixtureHandler { MetadataReply = (_, count) => count == 1 ? WithBody(body) : null };
        using HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        IImageBoard provider = Provider(eightKun, client, new JsonResponseReader(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), clock));
        Task pending = CheckAsync(provider, eightKun, catalog);
        await gate.Entered.Task.WaitAsync(TestWait.Timeout);
        clock.Advance(TimeSpan.FromSeconds(5));
        await pending.WaitAsync(TestWait.Timeout);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(catalog ? 0 : 1, handler.ThreadRequests);
        Assert.Equal(catalog ? 1 : 0, handler.CatalogRequests);

        IBoard board = provider.GetBoard(new Uri(BoardUrl(eightKun)));
        Assert.Single(await board.GetThreadsAsync());
        Assert.Equal("Synthetic board", board.Name);
        Assert.Equal(2, handler.MetadataRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionalMetadataBodyIoFailureDoesNotBlockThreadOrCatalog(bool eightKun, bool catalog)
    {
        using FixtureHandler handler = new FixtureHandler
        {
            MetadataReply = (_, _) => WithBody(new StreamContent(new FailingStream()))
        };
        using HttpClient client = new HttpClient(handler);
        IImageBoard provider = Provider(eightKun, client);

        await CheckAsync(provider, eightKun, catalog).WaitAsync(TestWait.Timeout);

        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(catalog ? 0 : 1, handler.ThreadRequests);
        Assert.Equal(catalog ? 1 : 0, handler.CatalogRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedJsonBodyIsRejectedByTheRealProvider(bool eightKun, bool catalog)
    {
        using FixtureHandler handler = new FixtureHandler();
        string oversized = new string(' ', 512) + (catalog ? "[]" : EmptyThread);
        if (catalog) handler.CatalogReply = () => Json(oversized);
        else handler.ThreadReply = (_, _, _) => Task.FromResult(Json(oversized));
        using HttpClient client = new HttpClient(handler);
        IImageBoard provider = Provider(eightKun, client, new JsonResponseReader(maxBytes: 256));

        await Assert.ThrowsAsync<InvalidDataException>(() => CheckAsync(provider, eightKun, catalog));

        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(0, handler.MediaRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyHeaderDeadlinesBoundRealThreadRequestsAndDrainEachAttempt(bool eightKun)
    {
        ManualTime clock = new ManualTime();
        CancellationGate[] gates = [new CancellationGate(), new CancellationGate(), new CancellationGate()];
        using FixtureHandler handler = new FixtureHandler
        {
            ThreadReply = async (count, _, token) =>
            {
                await gates[count - 1].BlockAsync(token);
                throw new InvalidOperationException("A blocked header request cannot complete normally.");
            }
        };
        using HttpClient transport = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using HttpClient client = ProviderHttpClientFactory.CreatePolicyClient(transport, eightKun ? "8kun" : "4chan", new RequestPolicyState(clock, () => 0));
        IThread thread = Provider(eightKun, client).GetThread(new Uri(ThreadUrl(eightKun)));
        Task pending = thread.checkThreadAsync(new DownloadQueue(1, 1024 * 1024));
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        int elapsedSeconds = 1;
        for (int attempt = 0; attempt < gates.Length; attempt++)
        {
            await gates[attempt].Entered.Task.WaitAsync(TestWait.Timeout);
            clock.Advance(TimeSpan.FromSeconds(10));
            elapsedSeconds += 10;
            await gates[attempt].Finished.Task.WaitAsync(TestWait.Timeout);
            if (attempt + 1 < gates.Length)
            {
                int backoffSeconds = attempt + 1;
                elapsedSeconds += backoffSeconds;
                await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(elapsedSeconds));
                clock.Advance(TimeSpan.FromSeconds(backoffSeconds));
            }
        }

        await Assert.ThrowsAsync<TimeoutException>(() => pending).WaitAsync(TestWait.Timeout);
        Assert.Equal(3, handler.ThreadRequests);
        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
    }

    private static async Task CheckAsync(IImageBoard provider, bool eightKun, bool catalog, CancellationToken cancellationToken = default)
    {
        if (catalog) await provider.GetBoard(new Uri(BoardUrl(eightKun))).GetThreadsAsync(cancellationToken);
        else await provider.GetThread(new Uri(ThreadUrl(eightKun))).checkThreadAsync(new DownloadQueue(1, 1024 * 1024), cancellationToken);
    }

    private static IImageBoard Provider(bool eightKun, HttpClient client, JsonResponseReader? reader = null) => eightKun
        ? new ManualEightKunProvider(client, reader)
        : new ManualFourChanProvider(client, reader);
    private static string ThreadUrl(bool eightKun) => eightKun ? "https://8kun.top/g/res/123.html" : "https://boards.4chan.org/g/thread/123";
    private static string BoardUrl(bool eightKun) => eightKun ? "https://8kun.top/g/" : "https://boards.4chan.org/g/";
    private static Registry NewRegistry(IImageBoard provider, DownloadQueue queue, string directory) =>
        new Registry([provider], queue, directory, TimeSpan.FromHours(1), 3600);
    private static HttpResponseMessage Media() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
    private static HttpResponseMessage WithBody(HttpContent content) => new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    private static HttpResponseMessage Json(string json, DateTimeOffset? modified = null)
    {
        HttpResponseMessage response = WithBody(new StringContent(json, Encoding.UTF8, "application/json"));
        response.Content.Headers.LastModified = modified;
        return response;
    }

    private interface IManualThread
    {
        AsyncStepGate Clock { get; }
        int CompletedChecks { get; }
        int RemovalRequests { get; }
    }

    private sealed class ManualFourChanProvider(HttpClient client, JsonResponseReader? reader) : FourChanImageBoard(client, reader)
    {
        public override IThread GetThread(Uri uri)
        {
            IThread passive = base.GetThread(uri);
            return new ManualFourChanThread(passive.Board, passive.ThreadUri);
        }
    }

    private sealed class ManualEightKunProvider(HttpClient client, JsonResponseReader? reader) : EightKunImageBoard(client, reader)
    {
        public override IThread GetThread(Uri uri)
        {
            IThread passive = base.GetThread(uri);
            return new ManualEightKunThread(passive.Board, passive.ThreadUri);
        }
    }

    private sealed class ManualFourChanThread(IBoard board, Uri uri) : FourChanThread(board, uri), IManualThread, IThread
    {
        private int _completedChecks;
        private int _removalRequests;
        public AsyncStepGate Clock { get; } = new AsyncStepGate();
        public int CompletedChecks => Volatile.Read(ref _completedChecks);
        public int RemovalRequests => Volatile.Read(ref _removalRequests);
        IDisposable IThread.RegisterRemovalSink(Action<IThread> sink) => base.RegisterRemovalSink(thread =>
        {
            Interlocked.Increment(ref _removalRequests);
            sink(thread);
        });
        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            try { await base.checkThreadAsync(queue, cancellationToken); }
            finally { Interlocked.Increment(ref _completedChecks); }
        }
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }

    private sealed class ManualEightKunThread(IBoard board, Uri uri) : EightKunThread(board, uri), IManualThread, IThread
    {
        private int _completedChecks;
        private int _removalRequests;
        public AsyncStepGate Clock { get; } = new AsyncStepGate();
        public int CompletedChecks => Volatile.Read(ref _completedChecks);
        public int RemovalRequests => Volatile.Read(ref _removalRequests);
        IDisposable IThread.RegisterRemovalSink(Action<IThread> sink) => base.RegisterRemovalSink(thread =>
        {
            Interlocked.Increment(ref _removalRequests);
            sink(thread);
        });
        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            try { await base.checkThreadAsync(queue, cancellationToken); }
            finally { Interlocked.Increment(ref _completedChecks); }
        }
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private int _metadataRequests;
        private int _catalogRequests;
        private int _threadRequests;
        private int _mediaRequests;
        public Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ThreadReply { get; set; }
        public Func<int, CancellationToken, Task<HttpResponseMessage>>? MediaReply { get; set; }
        public Func<HttpRequestMessage, int, HttpResponseMessage?>? MetadataReply { get; set; }
        public Func<HttpResponseMessage>? CatalogReply { get; set; }
        public bool ReportSending { get; init; }
        public ConcurrentQueue<DateTimeOffset?> Conditions { get; } = new ConcurrentQueue<DateTimeOffset?>();
        public int MetadataRequests => Volatile.Read(ref _metadataRequests);
        public int CatalogRequests => Volatile.Read(ref _catalogRequests);
        public int ThreadRequests => Volatile.Read(ref _threadRequests);
        public int MediaRequests => Volatile.Read(ref _mediaRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            if (uri.AbsolutePath == "/boards.json")
            {
                int count = Interlocked.Increment(ref _metadataRequests);
                HttpResponseMessage? customized = MetadataReply?.Invoke(request, count);
                return Task.FromResult(customized ?? Json(uri.Host == "8kun.top"
                    ? "[{\"uri\":\"g\",\"title\":\"Synthetic board\"}]"
                    : "{\"boards\":[{\"board\":\"g\",\"title\":\"Synthetic board\"}]}"));
            }
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _catalogRequests);
                ReportProgress(request);
                return Task.FromResult(CatalogReply?.Invoke() ?? Json("[{\"page\":1,\"threads\":[{\"no\":123}]}]"));
            }
            if (uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
            {
                int count = Interlocked.Increment(ref _threadRequests);
                Conditions.Enqueue(request.Headers.IfModifiedSince);
                ReportProgress(request);
                return ThreadReply?.Invoke(count, request, cancellationToken) ?? Task.FromResult(Json(EmptyThread));
            }
            if (uri.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal))
            {
                int count = Interlocked.Increment(ref _mediaRequests);
                return MediaReply?.Invoke(count, cancellationToken) ?? Task.FromResult(Media());
            }
            throw new InvalidOperationException("Unexpected synthetic route: " + uri);
        }

        private void ReportProgress(HttpRequestMessage request)
        {
            if (ReportSending && request.Options.TryGetValue(RequestPolicyHandler.ObserverKey, out Action<RequestPolicyProgress>? observer))
                observer(new RequestPolicyProgress(RequestPolicyPhase.Sending, null, 1));
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        private int _readAttempts;
        public int ReadAttempts => Volatile.Read(ref _readAttempts);
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Increment(ref _readAttempts);
            throw new InvalidOperationException("A 304 response must not read its body.");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CancellationGate
    {
        public TaskCompletionSource Entered { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task BlockAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class BlockedContent(CancellationGate gate) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockedStream(gate));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => gate.BlockAsync(CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => gate.BlockAsync(cancellationToken);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockedStream(CancellationGate gate) : ReadOnlyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.BlockAsync(cancellationToken);
            return 0;
        }
    }

    private sealed class FailingStream : ReadOnlyStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Synthetic truncated metadata response."));
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new object();
        private readonly List<ManualTimer> _timers = new List<ManualTimer>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => Modified + TimeSpan.FromTicks(GetTimestamp());
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
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToList();
                foreach (ManualTimer timer in due)
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + timer.Period.Ticks;
            }
            foreach (ManualTimer timer in due) timer.Callback(timer.State);
        }
        public Task WaitForTimerAtAsync(TimeSpan elapsed) => TestWait.UntilAsync(() =>
        {
            lock (_sync) { return _timers.Any(timer => !timer.Disposed && timer.Due == elapsed.Ticks); }
        }, $"No HTTP timer was scheduled for {elapsed}.");

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (Disposed) return false;
                    ChangeLocked(dueTime, period);
                    return true;
                }
            }
            public void ChangeLocked(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period;
            }
            public void Dispose() { lock (owner._sync) { Disposed = true; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

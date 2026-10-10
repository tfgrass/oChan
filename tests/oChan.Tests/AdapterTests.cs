using System.Net;
using System.Text;
using System.Text.Json;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class AdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealAdaptersArePassiveAndOneHundredCatalogCyclesKeepOneWatch(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        using AdapterHandler handler = new AdapterHandler();
        using HttpClient client = new HttpClient(handler);
        IImageBoard provider = Provider(eightKun, client);
        string threadUrl = ThreadUrl(eightKun);
        string boardUrl = BoardUrl(eightKun);
        _ = provider.GetThread(new Uri(threadUrl));
        _ = provider.GetBoard(new Uri(boardUrl));
        Assert.Equal(0, handler.TotalRequests);
        Assert.Same(client, provider.GetHttpClient());
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = new Registry([provider], queue, directory.Path, TimeSpan.FromHours(1), 3600);

        AdmissionResult firstThread = await registry.AddAsync(threadUrl);
        AdmissionResult firstBoard = await registry.AddAsync(boardUrl);
        AsyncStepGate threadClock = Assert.IsAssignableFrom<IManualThread>(firstThread.Thread).Clock;
        AsyncStepGate boardClock = Assert.IsAssignableFrom<IManualBoard>(firstBoard.Board).Clock;
        await TestWait.UntilAsync(() => threadClock.Waits == 1 && boardClock.Waits == 1);
        for (int cycle = 2; cycle <= 100; cycle++)
        {
            boardClock.Advance();
            await TestWait.UntilAsync(() => boardClock.Waits >= cycle);
        }

        Assert.Equal(100, handler.CatalogRequests);
        Assert.Equal(1, handler.ThreadRequests);
        Assert.Equal(1, handler.MetadataRequests);
        Assert.Same(firstThread.Thread, Assert.Single(registry.Threads));
        string canonical = firstThread.Thread!.Url;
        Assert.DoesNotContain("//", new Uri(canonical).AbsolutePath);
        Assert.DoesNotContain("#", canonical);
        Assert.Equal(AdmissionStatus.AlreadyPresent, (await registry.AddAsync(canonical)).Status);
        Assert.Equal(AdmissionStatus.AlreadyPresent, (await registry.AddAsync(firstBoard.Board!.BoardUri.ToString())).Status);
    }

    [Fact]
    public async Task BothRealAdaptersUseTheSessionQueueForInitialAndLaterMediaChecks()
    {
        using TempDirectory directory = new TempDirectory();
        using AdapterHandler handler = new AdapterHandler { IncludeMedia = true, BlockMedia = true };
        using HttpClient client = new HttpClient(handler);
        IImageBoard four = Provider(false, client);
        IImageBoard eight = Provider(true, client);
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = new Registry([four, eight], queue, directory.Path, TimeSpan.FromHours(1), 3600);
        try
        {
            AdmissionResult fourResult = await registry.AddAsync(ThreadUrl(false));
            AdmissionResult eightResult = await registry.AddAsync(ThreadUrl(true));
            AsyncStepGate fourClock = Assert.IsAssignableFrom<IManualThread>(fourResult.Thread).Clock;
            AsyncStepGate eightClock = Assert.IsAssignableFrom<IManualThread>(eightResult.Thread).Clock;
            await TestWait.UntilAsync(() => fourClock.Waits == 1 && eightClock.Waits == 1 && handler.MediaRequests == 1);
            fourClock.Advance();
            eightClock.Advance();
            await TestWait.UntilAsync(() => fourClock.Waits == 2 && eightClock.Waits == 2);
            Assert.Equal(4, handler.ThreadRequests);
            Assert.Equal(1, handler.MediaRequests);
            Assert.Equal(1, handler.PeakMediaRequests);

            handler.ReleaseMedia.TrySetResult();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(4, handler.MediaRequests);
            Assert.Equal(1, handler.PeakMediaRequests);
            Assert.Equal(4, Directory.GetFiles(directory.Path, "*.jpg", SearchOption.AllDirectories).Length);
        }
        finally { handler.ReleaseMedia.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealAdapterFirstHttpFailureDoesNotReleaseAdmission(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        using AdapterHandler handler = new AdapterHandler { FailFirstThreadRequest = true };
        using HttpClient client = new HttpClient(handler);
        IImageBoard provider = Provider(eightKun, client);
        await using Registry registry = new Registry([provider], new DownloadQueue(1, 1024 * 1024), directory.Path, TimeSpan.FromHours(1), 3600);
        AdmissionResult result = await registry.AddAsync(ThreadUrl(eightKun));
        AsyncStepGate clock = Assert.IsAssignableFrom<IManualThread>(result.Thread).Clock;
        await TestWait.UntilAsync(() => clock.Waits == 1);
        Assert.Equal("Error", result.Thread!.Status);
        Assert.Equal(AdmissionStatus.AlreadyPresent, (await registry.AddAsync(ThreadUrl(eightKun))).Status);
        Assert.Equal(1, handler.ThreadRequests);
        clock.Advance();
        await TestWait.UntilAsync(() => clock.Waits == 2);
        Assert.Equal(2, handler.ThreadRequests);
        Assert.Same(result.Thread, Assert.Single(registry.Threads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoardRemoveAndReaddCreatesNewLifetimeAndIgnoresStaleRemoval(bool eightKun)
    {
        using TempDirectory directory = new TempDirectory();
        using AdapterHandler handler = new AdapterHandler();
        using HttpClient client = new HttpClient(handler);
        IImageBoard provider = Provider(eightKun, client);
        await using Registry registry = new Registry([provider], new DownloadQueue(1, 1024 * 1024), directory.Path, TimeSpan.FromHours(1), 3600);
        IBoard first = (await registry.AddAsync(BoardUrl(eightKun))).Board!;
        await TestWait.UntilAsync(() => Assert.IsAssignableFrom<IManualBoard>(first).Clock.Waits == 1 && registry.Threads.Count == 1);
        IThread discovered = Assert.Single(registry.Threads);
        await registry.RemoveBoardAsync(first).WaitAsync(TestWait.Timeout);
        Assert.Same(discovered, Assert.Single(registry.Threads));
        IBoard second = (await registry.AddAsync(BoardUrl(eightKun))).Board!;
        Assert.NotSame(first, second);
        await TestWait.UntilAsync(() => Assert.IsAssignableFrom<IManualBoard>(second).Clock.Waits == 1);
        await registry.RemoveBoardAsync(first);
        Assert.Same(second, Assert.Single(registry.Boards));
        Assert.Same(discovered, Assert.Single(registry.Threads));
        Assert.Equal(1, handler.MetadataRequests);
    }

    [Theory]
    [InlineData(false, "http")]
    [InlineData(false, "json")]
    [InlineData(true, "http")]
    [InlineData(true, "json")]
    public async Task OptionalMetadataFailureDoesNotBlockThreadOrCatalogAndCanRecover(bool eightKun, string failure)
    {
        using TempDirectory directory = new();
        using AdapterHandler handler = new() { MetadataFailure = failure };
        using HttpClient client = new(handler);
        IImageBoard provider = Provider(eightKun, client);
        await using Registry registry = new([provider], new DownloadQueue(1, 1024 * 1024), directory.Path, TimeSpan.FromHours(1), 3600);
        AdmissionResult threadResult = await registry.AddAsync(ThreadUrl(eightKun));
        AdmissionResult boardResult = await registry.AddAsync(BoardUrl(eightKun));
        AsyncStepGate threadClock = Assert.IsAssignableFrom<IManualThread>(threadResult.Thread).Clock;
        AsyncStepGate boardClock = Assert.IsAssignableFrom<IManualBoard>(boardResult.Board).Clock;
        await TestWait.UntilAsync(() => threadClock.Waits == 1 && boardClock.Waits == 1);

        Assert.Equal(1, handler.ThreadRequests);
        Assert.Equal(1, handler.CatalogRequests);
        Assert.NotEqual("Error", threadResult.Thread!.Status);
        Assert.Equal("g", boardResult.Board!.Name);
        handler.MetadataFailure = null;
        IEnumerable<IThread> recoveredCatalog = await boardResult.Board.GetThreadsAsync();
        Assert.Single(recoveredCatalog);
        Assert.Equal("Synthetic", boardResult.Board.Name);
        Assert.Equal(2, handler.CatalogRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentMediaWithTheSameOriginalFilenameKeepBothFiles(bool eightKun)
    {
        using TempDirectory directory = new();
        using AdapterHandler handler = new() { IncludeMedia = true, UseSharedOriginalFilename = true };
        using HttpClient client = new(handler);
        IImageBoard provider = Provider(eightKun, client);
        DownloadQueue queue = new(1, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromHours(1), 3600);
        AdmissionResult result = await registry.AddAsync(ThreadUrl(eightKun));
        AsyncStepGate clock = Assert.IsAssignableFrom<IManualThread>(result.Thread).Clock;
        await TestWait.UntilAsync(() => clock.Waits == 1);
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        clock.Advance();
        await TestWait.UntilAsync(() => clock.Waits == 2);
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

        Assert.Equal(2, handler.MediaRequests);
        string[] mediaFiles = Directory.GetFiles(directory.Path, "*.jpg", SearchOption.AllDirectories);
        Assert.Equal(2, mediaFiles.Length);
        Assert.NotEqual(Path.GetFileName(mediaFiles[0]), Path.GetFileName(mediaFiles[1]));
        Assert.All(mediaFiles, path => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path)));
    }

    private static IImageBoard Provider(bool eightKun, HttpClient client)
        => eightKun ? new ManualEightKunProvider(client) : new ManualFourChanProvider(client);
    private static string ThreadUrl(bool eightKun) => eightKun
        ? "https://8kun.top/g/res/456.html#q789"
        : "https://boards.4channel.org/g/thread/123/a-subject#p789";
    private static string BoardUrl(bool eightKun) => eightKun
        ? "https://8kun.top/g/catalog.html"
        : "https://boards.4chan.org/g/catalog/";

    private interface IManualThread { AsyncStepGate Clock { get; } }
    private interface IManualBoard { AsyncStepGate Clock { get; } }

    private sealed class ManualFourChanProvider(HttpClient client) : FourChanImageBoard(client)
    {
        public override IBoard GetBoard(Uri uri) => new ManualFourChanBoard(this, base.GetBoard(uri).BoardUri);
        public override IThread GetThread(Uri uri)
        {
            IThread passive = base.GetThread(uri);
            return new ManualFourChanThread(passive.Board, passive.ThreadUri);
        }
    }
    private sealed class ManualEightKunProvider(HttpClient client) : EightKunImageBoard(client)
    {
        public override IBoard GetBoard(Uri uri) => new ManualEightKunBoard(this, base.GetBoard(uri).BoardUri);
        public override IThread GetThread(Uri uri)
        {
            IThread passive = base.GetThread(uri);
            return new ManualEightKunThread(passive.Board, passive.ThreadUri);
        }
    }
    private sealed class ManualFourChanThread(IBoard board, Uri uri) : FourChanThread(board, uri), IManualThread
    {
        public AsyncStepGate Clock { get; } = new();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }
    private sealed class ManualEightKunThread(IBoard board, Uri uri) : EightKunThread(board, uri), IManualThread
    {
        public AsyncStepGate Clock { get; } = new();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }
    private sealed class ManualFourChanBoard(IImageBoard provider, Uri uri) : FourChanBoard(provider, uri), IManualBoard
    {
        public AsyncStepGate Clock { get; } = new();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }
    private sealed class ManualEightKunBoard(IImageBoard provider, Uri uri) : EightKunBoard(provider, uri), IManualBoard
    {
        public AsyncStepGate Clock { get; } = new();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }

    private sealed class AdapterHandler : HttpMessageHandler
    {
        private int _total, _metadata, _catalog, _thread, _media, _activeMedia, _peakMedia;
        public bool IncludeMedia { get; init; }
        public bool BlockMedia { get; init; }
        public bool FailFirstThreadRequest { get; init; }
        public bool UseSharedOriginalFilename { get; init; }
        public string? MetadataFailure { get; set; }
        public TaskCompletionSource ReleaseMedia { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TotalRequests => Volatile.Read(ref _total);
        public int MetadataRequests => Volatile.Read(ref _metadata);
        public int CatalogRequests => Volatile.Read(ref _catalog);
        public int ThreadRequests => Volatile.Read(ref _thread);
        public int MediaRequests => Volatile.Read(ref _media);
        public int PeakMediaRequests => Volatile.Read(ref _peakMedia);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _total);
            Uri uri = request.RequestUri!;
            if (uri.AbsolutePath == "/boards.json")
            {
                Interlocked.Increment(ref _metadata);
                if (MetadataFailure == "http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (MetadataFailure == "json") return Json("{ invalid metadata");
                return Json(uri.Host == "8kun.top" ? "[{\"uri\":\"g\",\"title\":\"Synthetic\"}]" : "{\"boards\":[{\"board\":\"g\",\"title\":\"Synthetic\"}]}");
            }
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _catalog);
                int id = uri.Host == "8kun.top" ? 456 : 123;
                return Json(JsonSerializer.Serialize(new[] { new { page = 1, threads = new[] { new { no = id } } } }));
            }
            if (uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
            {
                int count = Interlocked.Increment(ref _thread);
                if (count == 1 && FailFirstThreadRequest) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (!IncludeMedia) return Json("{\"posts\":[{\"no\":123,\"sub\":\"Synthetic\"}]}");
                int tim = 1000 + count;
                string filename = UseSharedOriginalFilename ? "same-original-name" : "synthetic-" + tim;
                return Json(JsonSerializer.Serialize(new { posts = new[] { new { no = 123, tim, ext = ".jpg", filename } } }));
            }
            if (uri.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _media);
                int active = Interlocked.Increment(ref _activeMedia);
                int old;
                do { old = _peakMedia; if (old >= active) break; }
                while (Interlocked.CompareExchange(ref _peakMedia, active, old) != old);
                try
                {
                    if (BlockMedia) await ReleaseMedia.Task.WaitAsync(cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
                }
                finally { Interlocked.Decrement(ref _activeMedia); }
            }
            throw new InvalidOperationException("Unexpected synthetic HTTP route: " + uri);
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

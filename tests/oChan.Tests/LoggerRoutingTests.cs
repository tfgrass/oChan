using System.Collections.Concurrent;
using System.Net;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Http;
using oChan.Interfaces;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace oChan.Tests;

// One regression deliberately installs a process-global logger to catch accidental
// use by the library. It must not overlap other tests changing application globals.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoggerRoutingCollection
{
    public const string Name = "Logger routing";
}

[Collection(LoggerRoutingCollection.Name)]
public sealed class LoggerRoutingTests
{
    [Fact]
    public async Task DefaultLibraryOperationsLeaveTheProcessLoggerUntouchedAndSilent()
    {
        RecordingSink globalSink = new RecordingSink();
        using Logger processLogger = CreateLogger(globalSink);
        ILogger previous = Log.Logger;
        Log.Logger = processLogger;
        try
        {
            using TempDirectory directory = new TempDirectory();
            using StubImageBoard provider = new StubImageBoard();
            ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"));
            DownloadQueue queue = new DownloadQueue(1, long.MaxValue);
            queue.UpdateMaxBandwidth(long.MaxValue - 1);
            Assert.True(queue.TryEnqueue(CreateItem(provider, thread, directory.Path, "silent")));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);

            using HttpClient metadataClient = CreateMetadataClient();
            FourChanImageBoard builtIn = new FourChanImageBoard(metadataClient);
            Assert.Same(Logger.None, builtIn.Logger);
            Assert.Empty(await builtIn.GetBoard(new Uri("https://boards.4chan.org/g/")).GetThreadsAsync());
            using HttpClient transport = CreateSuccessClient();
            using HttpClient policyClient = ProviderHttpClientFactory.CreatePolicyClient(transport, "silent-test", new RequestPolicyState());
            using HttpRequestMessage request = CreateObservedRequest();
            using HttpResponseMessage response = await policyClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Throws<ArgumentNullException>(() => new DownloadItem(null!, directory.Path, provider, thread, "bad"));

            Assert.Same(processLogger, Log.Logger);
            Assert.Empty(globalSink.Events);
            Assert.Equal(0, globalSink.DisposeCount);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task QueueWorkersAndLimiterRouteToTheirOwnCallerLogger()
    {
        RecordingSink firstSink = new RecordingSink();
        RecordingSink secondSink = new RecordingSink();
        using Logger firstLogger = CreateLogger(firstSink);
        using Logger secondLogger = CreateLogger(secondSink);
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard firstProvider = new StubImageBoard();
        using StubImageBoard secondProvider = new StubImageBoard(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        ProbeThread firstThread = new ProbeThread(firstProvider.Board, new Uri("https://stub.test/g/thread/123"));
        ProbeThread secondThread = new ProbeThread(secondProvider.Board, new Uri("https://stub.test/g/thread/456"));
        DownloadQueue firstQueue = new DownloadQueue(1, long.MaxValue, logger: firstLogger);
        DownloadQueue secondQueue = new DownloadQueue(1, long.MaxValue, logger: secondLogger);
        firstQueue.UpdateMaxBandwidth(long.MaxValue - 1);
        Assert.True(firstQueue.TryEnqueue(CreateItem(firstProvider, firstThread, directory.Path, "first")));
        Assert.True(secondQueue.TryEnqueue(CreateItem(secondProvider, secondThread, directory.Path, "second")));
        await Task.WhenAll(firstQueue.WaitForIdleAsync(), secondQueue.WaitForIdleAsync()).WaitAsync(TestWait.Timeout);
        await Task.WhenAll(firstQueue.StopAllAsync(), secondQueue.StopAllAsync()).WaitAsync(TestWait.Timeout);

        Assert.Contains(firstSink.Events, entry => entry.MessageTemplate.Text.StartsWith("Saved verified media", StringComparison.Ordinal));
        Assert.Contains(firstSink.Events, entry => entry.MessageTemplate.Text.StartsWith("Updated bandwidth", StringComparison.Ordinal));
        Assert.DoesNotContain(firstSink.Events, entry => entry.Level >= LogEventLevel.Error);
        Assert.Contains(secondSink.Events, entry => entry.MessageTemplate.Text == "Error downloading {DownloadUri}");
        Assert.DoesNotContain(secondSink.Events, entry => entry.MessageTemplate.Text.StartsWith("Saved verified media", StringComparison.Ordinal));
        Assert.Equal(0, firstSink.DisposeCount);
        Assert.Equal(0, secondSink.DisposeCount);
        firstLogger.Information("Caller still owns the logger");
        Assert.Contains(firstSink.Events, entry => entry.MessageTemplate.Text == "Caller still owns the logger");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderMetadataFailuresUseTheProviderLogger(bool eightKun)
    {
        RecordingSink sink = new RecordingSink();
        using Logger logger = CreateLogger(sink);
        using HttpClient client = CreateMetadataClient();
        IImageBoard provider = eightKun
            ? new EightKunImageBoard(client, logger: logger)
            : new FourChanImageBoard(client, logger: logger);
        Uri uri = new Uri(eightKun ? "https://8kun.top/tech/" : "https://boards.4chan.org/g/");

        Assert.Empty(await provider.GetBoard(uri).GetThreadsAsync());

        Assert.Same(logger, provider.Logger);
        LogEvent warning = Assert.Single(sink.Events, entry => entry.MessageTemplate.Text.StartsWith("Could not fetch the name", StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.IsType<HttpRequestException>(warning.Exception);
        Assert.Equal(0, sink.DisposeCount);
    }

    [Fact]
    public async Task PolicyObserverFailuresUseTheAssignedLoggerAndDoNotDisposeBorrowedObjects()
    {
        RecordingSink sink = new RecordingSink();
        using Logger logger = CreateLogger(sink);
        using HttpClient transport = CreateSuccessClient();
        using (HttpClient policyClient = ProviderHttpClientFactory.CreatePolicyClient(transport, "logger-test", new RequestPolicyState(), logger))
        {
            using HttpRequestMessage request = CreateObservedRequest();
            using HttpResponseMessage response = await policyClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Contains(sink.Events, entry => entry.MessageTemplate.Text == "HTTP policy observer failed");
        Assert.Equal(0, sink.DisposeCount);
        using HttpResponseMessage directResponse = await transport.GetAsync("https://logger.test/direct");
        Assert.Equal(HttpStatusCode.OK, directResponse.StatusCode);
    }

    [Fact]
    public void DefaultClientCachesDoNotRouteOneHostsDiagnosticsIntoAnotherHostsLogger()
    {
        using Logger first = CreateLogger(new RecordingSink());
        using Logger second = CreateLogger(new RecordingSink());
        HttpClient firstClient = ProviderHttpClientFactory.GetDefaultClient("4chan", first);

        Assert.Same(firstClient, ProviderHttpClientFactory.GetDefaultClient("4chan", first));
        Assert.Same(firstClient, new FourChanImageBoard(logger: first).GetHttpClient());
        Assert.NotSame(firstClient, ProviderHttpClientFactory.GetDefaultClient("4chan", second));
        Assert.NotSame(firstClient, ProviderHttpClientFactory.GetDefaultClient("4chan"));
    }

    [Fact]
    public async Task BlockedQueueObserverDoesNotDelayWorkersOtherObserversOrStop()
    {
        using TempDirectory directory = new TempDirectory();
        TaskCompletionSource<HttpResponseMessage> download = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using StubImageBoard provider = new StubImageBoard(new DelegateHandler((_, _) => download.Task));
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"));
        DownloadQueue queue = new DownloadQueue(1, long.MaxValue);
        using ManualResetEventSlim release = new ManualResetEventSlim(false);
        TaskCompletionSource blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action blockingObserver = () =>
        {
            blocked.TrySetResult();
            release.Wait();
            returned.TrySetResult();
        };
        Action healthyObserver = () => healthy.TrySetResult();
        queue.StateChanged += blockingObserver;
        queue.StateChanged += healthyObserver;
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(provider, thread, directory.Path, "event")));
            await Task.WhenAll(blocked.Task, healthy.Task).WaitAsync(TestWait.Timeout);
            download.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
            Assert.False(returned.Task.IsCompleted);
            Assert.Equal(new DownloadCounts(0, 0), queue.GetThreadCounts([thread])[thread]);
        }
        finally
        {
            queue.StateChanged -= blockingObserver;
            queue.StateChanged -= healthyObserver;
            download.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            release.Set();
            if (blocked.Task.IsCompleted) await returned.Task.WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private static Logger CreateLogger(RecordingSink sink) => new LoggerConfiguration()
        .MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static DownloadItem CreateItem(IImageBoard provider, IThread thread, string root, string id) => new DownloadItem(
        new Uri($"https://logger.test/{id}.jpg"), Path.Combine(root, id, id + ".jpg"), provider, thread, id, expectedSize: 3);

    private static HttpClient CreateSuccessClient() => new HttpClient(new DelegateHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));

    private static HttpClient CreateMetadataClient() => new HttpClient(new DelegateHandler((request, _) =>
        Task.FromResult(request.RequestUri!.AbsolutePath == "/boards.json"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") })));

    private static HttpRequestMessage CreateObservedRequest()
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://logger.test/observer");
        request.Options.Set(RequestPolicyHandler.ObserverKey, _ => throw new InvalidOperationException("Synthetic observer failure"));
        return request;
    }

    private sealed class RecordingSink : ILogEventSink, IDisposable
    {
        private int _disposeCount;
        public ConcurrentQueue<LogEvent> Events { get; } = new ConcurrentQueue<LogEvent>();
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}

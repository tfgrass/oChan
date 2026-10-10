using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using oChan.Boards.Base;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ochan-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class TestWait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string description = "Expected asynchronous state was not reached.")
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < Timeout)
            await Task.Delay(5);
        Assert.True(condition(), description);
    }
}

internal sealed class AsyncStepGate
{
    private readonly SemaphoreSlim _steps = new(0);
    private int _waits;
    public int Waits => Volatile.Read(ref _waits);
    public void Advance() => _steps.Release();
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waits);
        await _steps.WaitAsync(cancellationToken);
    }
}

internal sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => response(request, cancellationToken);
}

internal sealed class StubImageBoard : IImageBoard, IDisposable
{
    private readonly HttpClient _client;
    public StubImageBoard(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));
        Board = new ProbeBoard(this);
    }

    public string Name => "stub";
    public string NiceName => "Synthetic provider";
    public Uri BaseUri => new("https://stub.test/");
    public ProbeBoard Board { get; }
    public Func<Uri, IThread>? ThreadFactory { get; set; }
    public Func<Uri, IBoard>? BoardFactory { get; set; }
    public bool CanHandle(Uri uri) => uri.Scheme == "https" && uri.Host == BaseUri.Host;
    public bool IsThreadUri(Uri uri) => Regex.IsMatch(uri.AbsolutePath, @"^/\w+/thread/\d+(?:/.*)?$");
    public bool IsBoardUri(Uri uri) => Regex.IsMatch(uri.AbsolutePath, @"^/\w+/(?:catalog(?:\.html)?)?$");
    public IBoard GetBoard(Uri uri) => BoardFactory?.Invoke(uri) ?? Board;
    public IThread GetThread(Uri uri) => ThreadFactory?.Invoke(uri) ?? new ProbeThread(Board, uri);
    public HttpClient GetHttpClient() => _client;
    public void Dispose() => _client.Dispose();
}

internal class ProbeThread : BaseThread
{
    private int _checks;
    private int _active;
    private int _maximumActive;
    public ProbeThread(IBoard board, Uri uri)
    {
        Board = board;
        ThreadId = Regex.Match(uri.AbsolutePath, @"/thread/(\d+)").Groups[1].Value;
        ThreadUri = new Uri($"{board.ImageBoard.BaseUri.GetLeftPart(UriPartial.Authority)}/{board.BoardCode}/thread/{ThreadId}");
    }
    public override IBoard Board { get; }
    public override string ThreadId { get; }
    public override string NiceName => ThreadId;
    public override Uri ThreadUri { get; }
    public AsyncStepGate Clock { get; } = new();
    public ConcurrentQueue<TimeSpan> RequestedIntervals { get; } = new();
    public Func<Task>? OnCheck { get; set; }
    public Func<CancellationToken, Task>? OnCheckWithCancellation { get; set; }
    public int Checks => Volatile.Read(ref _checks);
    public int MaximumActive => Volatile.Read(ref _maximumActive);
    public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _checks);
        int active = Interlocked.Increment(ref _active);
        int current;
        do { current = _maximumActive; if (current >= active) break; }
        while (Interlocked.CompareExchange(ref _maximumActive, active, current) != current);
        try
        {
            if (OnCheckWithCancellation is not null) await OnCheckWithCancellation(cancellationToken);
            else if (OnCheck is not null) await OnCheck();
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        RequestedIntervals.Enqueue(interval);
        return Clock.WaitAsync(cancellationToken);
    }
}

internal class ProbeBoard(IImageBoard imageBoard) : BaseBoard
{
    private int _checks;
    private int _active;
    private int _maximumActive;
    public override IImageBoard ImageBoard => imageBoard;
    public override string BoardCode => "g";
    public override string Name { get; protected set; } = "Synthetic board";
    public override string NiceName => Name;
    public override Uri BoardUri => new(imageBoard.BaseUri, "/g/");
    public AsyncStepGate Clock { get; } = new();
    public ConcurrentQueue<TimeSpan> RequestedIntervals { get; } = new();
    public Func<Task<IEnumerable<IThread>>>? OnCheck { get; set; }
    public Func<CancellationToken, Task<IEnumerable<IThread>>>? OnCheckWithCancellation { get; set; }
    public int Checks => Volatile.Read(ref _checks);
    public int MaximumActive => Volatile.Read(ref _maximumActive);
    public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _checks);
        int active = Interlocked.Increment(ref _active);
        int current;
        do { current = _maximumActive; if (current >= active) break; }
        while (Interlocked.CompareExchange(ref _maximumActive, active, current) != current);
        try
        {
            if (OnCheckWithCancellation is not null) return await OnCheckWithCancellation(cancellationToken);
            return OnCheck is null ? [] : await OnCheck();
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        RequestedIntervals.Enqueue(interval);
        return Clock.WaitAsync(cancellationToken);
    }
}

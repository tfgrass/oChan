using System.Collections.Concurrent;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class RegistryTests
{
    private static Registry CreateRegistry(StubImageBoard provider, DownloadQueue queue, string directory)
        => new([provider], queue, directory, TimeSpan.FromMinutes(1), 60);

    [Fact]
    public async Task ConcurrentCanonicalThreadAddsStartOneWatchAndInvalidateTheSnapshot()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024);
        await using Registry registry = CreateRegistry(provider, queue, directory.Path);
        TaskCompletionSource changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.StateChanged += () => changed.TrySetResult();
        AdmissionResult[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            registry.AddAsync(index % 2 == 0 ? "https://stub.test/g/thread/123#p456" : "https://stub.test/g/thread/123/a-subject"))));

        Assert.Single(results, result => result.Status == AdmissionStatus.Added);
        Assert.Equal(31, results.Count(result => result.Status == AdmissionStatus.AlreadyPresent));
        ProbeThread thread = Assert.IsType<ProbeThread>(Assert.Single(registry.Threads));
        Assert.All(results, result => Assert.Same(thread, result.Thread));
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
        Assert.Equal(1, thread.Checks);
        await changed.Task.WaitAsync(TestWait.Timeout);
        Assert.Single(registry.Threads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectAdmissionAndCatalogDiscoveryConvergeOnOneWatch(bool directFirst)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024);
        ConcurrentBag<ProbeThread> createdThreads = new ConcurrentBag<ProbeThread>();
        provider.ThreadFactory = uri =>
        {
            ProbeThread thread = new ProbeThread(provider.Board, uri);
            createdThreads.Add(thread);
            return thread;
        };
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>(
            [provider.GetThread(new Uri("https://stub.test/g/thread/123"))]);
        await using Registry registry = CreateRegistry(provider, queue, directory.Path);
        if (directFirst) Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync("https://stub.test/g/thread/123")).Status);
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync("https://stub.test/g/catalog")).Status);
        await TestWait.UntilAsync(() => registry.Threads.Count == 1 && provider.Board.Clock.Waits == 1);
        AdmissionResult directResult = await registry.AddAsync("https://stub.test/g/thread/123/a-title#p456");
        Assert.Equal(AdmissionStatus.AlreadyPresent, directResult.Status);

        for (int cycle = 2; cycle <= 100; cycle++)
        {
            provider.Board.Clock.Advance();
            await TestWait.UntilAsync(() => provider.Board.Clock.Waits >= cycle);
        }

        ProbeThread watch = Assert.IsType<ProbeThread>(Assert.Single(registry.Threads));
        await TestWait.UntilAsync(() => watch.Clock.Waits == 1);
        Assert.Equal(100, provider.Board.Checks);
        Assert.Equal(1, watch.Checks);
        Assert.Equal(1, createdThreads.Sum(thread => thread.Checks));
        Assert.Same(watch, directResult.Thread);
        Assert.Equal(AdmissionStatus.AlreadyPresent, (await registry.AddAsync("https://stub.test/g/")).Status);
    }

    [Fact]
    public async Task InitialRequestFailureKeepsTheWatchAndDoesNotAllowAnotherStart()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"))
        {
            OnCheck = () => Task.FromException(new HttpRequestException("Synthetic initial request failure"))
        };
        provider.ThreadFactory = _ => thread;
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, 1024 * 1024), directory.Path);
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(thread.Url)).Status);
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
        Assert.Equal("Error", thread.Status);
        Assert.Equal(AdmissionStatus.AlreadyPresent, (await registry.AddAsync(thread.Url)).Status);
        Assert.Same(thread, Assert.Single(registry.Threads));
        Assert.Equal(1, thread.Checks);

        thread.Clock.Advance();
        await TestWait.UntilAsync(() => thread.Clock.Waits == 2);
        Assert.Equal(2, thread.Checks);
        Assert.Equal(1, thread.MaximumActive);
    }

    [Fact]
    public async Task SetupFailureRollsBackStartedPlanningAndAllowsExplicitRetry()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        Uri uri = new Uri("https://stub.test/g/thread/123");
        SetupFailureThread failed = new SetupFailureThread(provider.Board, uri);
        ProbeThread successful = new ProbeThread(provider.Board, uri);
        int attempts = 0;
        provider.ThreadFactory = _ => Interlocked.Increment(ref attempts) == 1 ? failed : successful;
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, 1024 * 1024), directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(uri.ToString()));
        Assert.Empty(registry.Threads);
        int failedChecksAfterRollback = failed.Checks;

        AdmissionResult retry = await registry.AddAsync(uri.ToString());
        Assert.Equal(AdmissionStatus.Added, retry.Status);
        Assert.Same(successful, retry.Thread);
        await TestWait.UntilAsync(() => successful.Clock.Waits == 1);
        failed.Clock.Advance();
        Assert.Equal(failedChecksAfterRollback, failed.Checks);
    }

    [Fact]
    public async Task RemoveThenImmediateAddWaitsForTheOldCheckAndRejectsStaleRemoval()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        TaskCompletionSource releaseOldCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeThread old = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"))
        {
            OnCheck = () => releaseOldCheck.Task
        };
        int created = 0;
        provider.ThreadFactory = uri => Interlocked.Increment(ref created) == 1 ? old : new ProbeThread(provider.Board, uri);
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, 1024 * 1024), directory.Path);
        try
        {
            await registry.AddAsync(old.Url);
            await TestWait.UntilAsync(() => old.Checks == 1);
            Task removal = registry.RemoveThreadAsync(old);
            await TestWait.UntilAsync(() => registry.Threads.Count == 0);
            Task<AdmissionResult> readmission = registry.AddAsync(old.Url);
            Assert.False(removal.IsCompleted);
            Assert.False(readmission.IsCompleted);

            releaseOldCheck.TrySetResult();
            await removal.WaitAsync(TestWait.Timeout);
            AdmissionResult result = await readmission.WaitAsync(TestWait.Timeout);
            ProbeThread replacement = Assert.IsType<ProbeThread>(result.Thread);
            Assert.Equal(AdmissionStatus.Added, result.Status);
            Assert.NotSame(old, replacement);
            await TestWait.UntilAsync(() => replacement.Clock.Waits == 1);
            await registry.RemoveThreadAsync(old);
            old.NotifyThreadRemoval(false);
            Assert.Same(replacement, Assert.Single(registry.Threads));
            Assert.Equal(1, old.Checks);
        }
        finally { releaseOldCheck.TrySetResult(); }
    }

    [Fact]
    public async Task ObserverFailureDoesNotRollBackAdmission()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, 1024 * 1024), directory.Path);
        int delivered = 0;
        registry.StateChanged += () => throw new InvalidOperationException("Synthetic observer failure");
        registry.StateChanged += () => Interlocked.Increment(ref delivered);
        AdmissionResult result = await registry.AddAsync("https://stub.test/g/thread/123");
        Assert.Equal(AdmissionStatus.Added, result.Status);
        Assert.Same(result.Thread, Assert.Single(registry.Threads));
        await TestWait.UntilAsync(() => Volatile.Read(ref delivered) > 0,
            "A failing subscriber must not prevent another subscriber from reading the state.");
        Assert.Same(result.Thread, Assert.Single(registry.Threads));
    }

    [Theory]
    [InlineData("not a URL", AdmissionStatus.Invalid)]
    [InlineData("file:///tmp/test", AdmissionStatus.Invalid)]
    [InlineData("https://unknown.test/g/thread/123", AdmissionStatus.Unsupported)]
    [InlineData("https://stub.test/unrecognized", AdmissionStatus.Invalid)]
    public async Task InvalidAndUnsupportedInputsCreateNoWatch(string url, AdmissionStatus expected)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        await using Registry registry = CreateRegistry(provider, new DownloadQueue(1, 1024 * 1024), directory.Path);
        Assert.Equal(expected, (await registry.AddAsync(url)).Status);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
    }

    private sealed class SetupFailureThread(IBoard board, Uri uri) : ProbeThread(board, uri), IThread
    {
        void IThread.StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
        {
            base.StartMonitoring(queue, outputDirectory, interval);
            throw new InvalidOperationException("Synthetic failure after planning was started");
        }
    }
}

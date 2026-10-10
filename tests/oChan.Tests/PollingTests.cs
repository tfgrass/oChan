using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class PollingTests
{
    [Fact]
    public async Task ThreadIntervalChangeAppliesToTheNextWaitWithoutStartingAnotherCheck()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(60));
            await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
            thread.UpdateMonitoringInterval(TimeSpan.FromSeconds(120));
            Assert.Equal(1, thread.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, thread.RequestedIntervals.ToArray());
            thread.Clock.Advance();
            await TestWait.UntilAsync(() => thread.Clock.Waits == 2);
            Assert.Equal(2, thread.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) }, thread.RequestedIntervals.ToArray());
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task BoardIntervalChangeAppliesToTheNextWaitWithoutStartingAnotherCheck()
    {
        using StubImageBoard provider = new();
        ProbeBoard board = provider.Board;
        try
        {
            board.StartMonitoring(60);
            await TestWait.UntilAsync(() => board.Clock.Waits == 1);
            board.UpdateMonitoringInterval(TimeSpan.FromSeconds(120));
            Assert.Equal(1, board.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, board.RequestedIntervals.ToArray());
            board.Clock.Advance();
            await TestWait.UntilAsync(() => board.Clock.Waits == 2);
            Assert.Equal(2, board.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) }, board.RequestedIntervals.ToArray());
        }
        finally { await board.StopMonitoringAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task MarkMediaPendingRemovesCompletionAndNotifiesTheVisibleProgress()
    {
        using StubImageBoard provider = new();
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123")) { TotalMediaCount = 1 };
        thread.MarkMediaAsDownloaded("123");
        Assert.Equal("Finished", thread.Status);
        TaskCompletionSource<string?> changed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.PropertyChanged += (_, change) =>
        {
            if (!thread.IsMediaDownloaded("123")) changed.TrySetResult(change.PropertyName);
        };

        thread.MarkMediaPending("123");

        Assert.False(thread.IsMediaDownloaded("123"));
        Assert.Equal(0, thread.DownloadedMediaCount);
        Assert.Equal("Pending", thread.Status);
        Assert.Equal(string.Empty, await changed.Task.WaitAsync(TestWait.Timeout));
        thread.MarkMediaPending("123");
        Assert.False(thread.IsMediaDownloaded("123"));
        Assert.Equal(0, thread.DownloadedMediaCount);
        Assert.Equal("Pending", thread.Status);
    }

    [Fact]
    public async Task DownloadedMediaIsSynchronizedAndExposedAsASnapshot()
    {
        using StubImageBoard provider = new();
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            thread.MarkMediaAsDownloaded(index.ToString());
            _ = thread.DownloadedMedia;
            Assert.True(thread.IsMediaDownloaded(index.ToString()));
        })));
        Assert.Equal(100, thread.DownloadedMediaCount);
        HashSet<string> snapshot = thread.DownloadedMedia;
        snapshot.Clear();
        Assert.Equal(100, thread.DownloadedMediaCount);
        HashSet<string> replacement = ["replacement"];
        thread.DownloadedMedia = replacement;
        replacement.Clear();
        Assert.True(thread.IsMediaDownloaded("replacement"));
    }

    [Fact]
    public async Task ThreadStartIsIdempotentAndSlowChecksDoNotOverlap()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123")) { OnCheck = () => release.Task };
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                thread.StartMonitoring(queue, directory.Path, TimeSpan.FromMilliseconds(1)))));
            await TestWait.UntilAsync(() => thread.Checks == 1);
            Assert.Equal(0, thread.Clock.Waits);
            Assert.Equal(1, thread.MaximumActive);
            release.TrySetResult();
            await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
            thread.Clock.Advance();
            await TestWait.UntilAsync(() => thread.Clock.Waits == 2);
            Assert.Equal(2, thread.Checks);
            Assert.Equal(1, thread.MaximumActive);
        }
        finally
        {
            release.TrySetResult();
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task BoardStartIsIdempotentAndSlowChecksDoNotOverlap()
    {
        using StubImageBoard provider = new StubImageBoard();
        TaskCompletionSource<IEnumerable<IThread>> release = new TaskCompletionSource<IEnumerable<IThread>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeBoard board = provider.Board;
        board.OnCheck = () => release.Task;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => board.StartMonitoring(1))));
            await TestWait.UntilAsync(() => board.Checks == 1);
            Assert.Equal(0, board.Clock.Waits);
            Assert.Equal(1, board.MaximumActive);
            release.TrySetResult([]);
            await TestWait.UntilAsync(() => board.Clock.Waits == 1);
            board.Clock.Advance();
            await TestWait.UntilAsync(() => board.Clock.Waits == 2);
            Assert.Equal(2, board.Checks);
            Assert.Equal(1, board.MaximumActive);
        }
        finally
        {
            release.TrySetResult([]);
            await board.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
        }
    }
}

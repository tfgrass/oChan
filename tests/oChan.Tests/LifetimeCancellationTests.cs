using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class LifetimeCancellationTests
{
    [Fact]
    public async Task RemoveCancelsTheOldCheckAndReadmissionWaitsForItsCleanup()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeThread old = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        bool? lateJobAccepted = null;
        old.OnCheckWithCancellation = async cancellationToken =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                await releaseCleanup.Task;
                DownloadItem late = new(new Uri("https://stub.test/media/late.bin"), Path.Combine(directory.Path, "late.bin"), provider, old, "late");
                lateJobAccepted = queue.TryEnqueue(late);
                throw;
            }
        };
        int created = 0;
        provider.ThreadFactory = uri => Interlocked.Increment(ref created) == 1 ? old : new ProbeThread(provider.Board, uri);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60);
        try
        {
            await registry.AddAsync(old.Url);
            await entered.Task.WaitAsync(TestWait.Timeout);
            Task removal = registry.RemoveThreadAsync(old);
            await cancelled.Task.WaitAsync(TestWait.Timeout);
            Task<AdmissionResult> readmission = registry.AddAsync(old.Url);
            Assert.False(removal.IsCompleted);
            Assert.False(readmission.IsCompleted);
            Assert.Empty(registry.Threads);

            releaseCleanup.TrySetResult();
            await removal.WaitAsync(TestWait.Timeout);
            AdmissionResult result = await readmission.WaitAsync(TestWait.Timeout);
            ProbeThread replacement = Assert.IsType<ProbeThread>(result.Thread);
            await TestWait.UntilAsync(() => replacement.Clock.Waits == 1);
            Assert.False(lateJobAccepted);
            Assert.False(File.Exists(Path.Combine(directory.Path, "late.bin")));
            Assert.NotEqual("Error", old.Status);
            Assert.Equal(1, old.Checks);
            Assert.Equal(1, replacement.Checks);
            old.NotifyThreadRemoval(false);
            await registry.RemoveThreadAsync(old);
            Assert.Same(replacement, Assert.Single(registry.Threads));
        }
        finally { releaseCleanup.TrySetResult(); }
    }

    [Fact]
    public async Task BoardRemovalSuppressesLateDiscoveryAndLeavesAnUnrelatedThreadRunning()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeThread late = new(provider.Board, new Uri("https://stub.test/g/thread/999"));
        provider.Board.OnCheckWithCancellation = async cancellationToken =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                await releaseCleanup.Task;
            }
            return [late];
        };
        await using Registry registry = new([provider], new DownloadQueue(1, 1024 * 1024), directory.Path, TimeSpan.FromMinutes(1), 60);
        try
        {
            ProbeThread independent = Assert.IsType<ProbeThread>((await registry.AddAsync("https://stub.test/g/thread/123")).Thread);
            await TestWait.UntilAsync(() => independent.Clock.Waits == 1);
            await registry.AddAsync("https://stub.test/g/catalog");
            await entered.Task.WaitAsync(TestWait.Timeout);
            Task removal = registry.RemoveBoardAsync(provider.Board);
            await cancelled.Task.WaitAsync(TestWait.Timeout);
            Assert.False(removal.IsCompleted);
            releaseCleanup.TrySetResult();
            await removal.WaitAsync(TestWait.Timeout);
            independent.Clock.Advance();
            await TestWait.UntilAsync(() => independent.Clock.Waits == 2);

            Assert.Equal(0, late.Checks);
            Assert.Equal(2, independent.Checks);
            Assert.Same(independent, Assert.Single(registry.Threads));
            Assert.Empty(registry.Boards);
        }
        finally { releaseCleanup.TrySetResult(); }
    }

    [Fact]
    public async Task RepeatedSessionStopCancelsEveryCheckAndWaitsForTheirCleanup()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        int cancelled = 0;
        provider.ThreadFactory = uri => new ProbeThread(provider.Board, uri)
        {
            OnCheckWithCancellation = async cancellationToken =>
            {
                Interlocked.Increment(ref entered);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref cancelled);
                    await releaseCleanup.Task;
                    throw;
                }
            }
        };
        DownloadQueue queue = new(2, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60);
        try
        {
            await registry.AddAsync("https://stub.test/g/thread/123");
            await registry.AddAsync("https://stub.test/g/thread/456");
            await TestWait.UntilAsync(() => Volatile.Read(ref entered) == 2);
            IThread[] previous = registry.Threads.ToArray();
            Task firstStop = registry.StopAsync();
            await TestWait.UntilAsync(() => Volatile.Read(ref cancelled) == 2);
            Task secondStop = registry.StopAsync();
            Assert.Same(firstStop, secondStop);
            Assert.False(firstStop.IsCompleted);
            releaseCleanup.TrySetResult();
            await Task.WhenAll(firstStop, secondStop).WaitAsync(TestWait.Timeout);

            Assert.Empty(registry.Threads);
            Assert.Empty(registry.Boards);
            Assert.True(queue.WaitForIdleAsync().IsCompletedSuccessfully);
            Assert.All(previous, thread => Assert.NotEqual("Error", thread.Status));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.AddAsync("https://stub.test/g/thread/789"));
        }
        finally { releaseCleanup.TrySetResult(); }
    }
}

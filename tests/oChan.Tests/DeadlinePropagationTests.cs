using oChan.Downloader;

namespace oChan.Tests;

public sealed class DeadlinePropagationTests
{
    [Fact]
    public async Task IdleDeadlineCancelsTheActualReadBeforeItsParentCallbacksFinish()
    {
        CapturedDeadlineTimeProvider clock = new CapturedDeadlineTimeProvider();
        using CallbackBarrier parentBarrier = new CallbackBarrier();
        using DeadlineBlockedStream body = new DeadlineBlockedStream(clock, parentBarrier);
        using IdleTimeoutReadStream stream = new IdleTimeoutReadStream(body, TimeSpan.FromSeconds(1), clock);

        Task<int> read = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
        Task fireDeadline = clock.FireAsync();
        try
        {
            await parentBarrier.Entered.Task.WaitAsync(TestWait.Timeout);
            // WaitAsync is registered after the barrier, which is registered after
            // the parent-to-child link. Its waiter therefore resumes while the
            // normal propagation callback is still behind this blocked callback.
            await AssertCompletesAsync(read);
            await Assert.ThrowsAsync<TimeoutException>(() => read);
            await body.CancellationSeen.Task.WaitAsync(TestWait.Timeout);
            Assert.False(parentBarrier.TimedOut, "The deadline barrier expired before cancellation reached the actual read.");
        }
        finally
        {
            parentBarrier.Release();
            await fireDeadline.WaitAsync(TestWait.Timeout);
            await parentBarrier.Exited.Task.WaitAsync(TestWait.Timeout);
            body.CompleteForCleanup();
            await ObserveCompletionAsync(read);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisposalDoNotBlockOrDropPendingChildCallbacks(bool cancelParent)
    {
        using CancellationTokenSource parent = new CancellationTokenSource();
        using AsyncCancellationLink linked = new AsyncCancellationLink(parent.Token);
        using CallbackBarrier childBarrier = new CallbackBarrier();
        TaskCompletionSource cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration earlier = linked.Token.Register(() => cancellationSeen.TrySetResult());
        using CancellationTokenRegistration later = linked.Token.Register(childBarrier.Block);
        Task cancellation = Task.Run(() =>
        {
            if (cancelParent)
                parent.Cancel();
            else
                linked.RequestCancellation();
        });
        Task? disposal = null;
        try
        {
            await childBarrier.Entered.Task.WaitAsync(TestWait.Timeout);
            await AssertCompletesAsync(cancellation);
            disposal = Task.Run(linked.Dispose);
            await AssertCompletesAsync(disposal);

            Assert.False(cancellationSeen.Task.IsCompleted,
                "The earlier callback must still be waiting behind the blocked later callback.");
            Assert.False(childBarrier.TimedOut, "Cancellation or disposal waited for a foreign child callback.");
        }
        finally
        {
            childBarrier.Release();
            await cancellation.WaitAsync(TestWait.Timeout);
            if (disposal != null)
                await disposal.WaitAsync(TestWait.Timeout);
            await childBarrier.Exited.Task.WaitAsync(TestWait.Timeout);
        }

        // Disposing a CTS before CancelAsync finishes would silently remove this
        // earlier registration while its later sibling was blocked.
        await cancellationSeen.Task.WaitAsync(TestWait.Timeout);
        Assert.False(childBarrier.TimedOut);
    }

    private static async Task AssertCompletesAsync(Task operation)
    {
        using CancellationTokenSource timeoutCancellation = new CancellationTokenSource();
        Task timeout = Task.Delay(TestWait.Timeout, timeoutCancellation.Token);
        Task completed = await Task.WhenAny(operation, timeout);
        timeoutCancellation.Cancel();
        Assert.Same(operation, completed);
    }

    private static async Task ObserveCompletionAsync(Task operation)
    {
        await AssertCompletesAsync(operation);
        try { await operation; }
        catch (Exception) { }
    }

    private sealed class CallbackBarrier : IDisposable
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private int _timedOut;
        public TaskCompletionSource Entered { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

        public void Block()
        {
            Entered.TrySetResult();
            try
            {
                if (!_release.Wait(TestWait.Timeout))
                    Interlocked.Exchange(ref _timedOut, 1);
            }
            finally
            {
                Exited.TrySetResult();
            }
        }

        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class DeadlineBlockedStream(CapturedDeadlineTimeProvider clock, CallbackBarrier parentBarrier) : MemoryStream
    {
        private readonly TaskCompletionSource<int> _read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _deadlineRegistration;
        private CancellationTokenRegistration _readRegistration;
        public TaskCompletionSource CancellationSeen { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _deadlineRegistration = clock.DeadlineToken.Register(parentBarrier.Block);
            _readRegistration = cancellationToken.Register(() =>
            {
                CancellationSeen.TrySetResult();
                _read.TrySetCanceled(cancellationToken);
            });
            return new ValueTask<int>(_read.Task);
        }

        public void CompleteForCleanup() => _read.TrySetResult(0);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _deadlineRegistration.Dispose();
                _readRegistration.Dispose();
                CompleteForCleanup();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class CapturedDeadlineTimeProvider : TimeProvider
    {
        private CapturedTimer? _timer;
        public CancellationToken DeadlineToken => (_timer ?? throw new InvalidOperationException("No deadline was created.")).Deadline.Token;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Null(_timer);
            // The pinned .NET 10 runtime passes the deadline CTS as timer state.
            // Capturing it lets the test place a barrier between its callbacks
            // without exposing or replacing the production linked token.
            CancellationTokenSource deadline = Assert.IsType<CancellationTokenSource>(state);
            _timer = new CapturedTimer(callback, deadline);
            return _timer;
        }

        public Task FireAsync()
        {
            CapturedTimer timer = _timer ?? throw new InvalidOperationException("No deadline was created.");
            return Task.Run(timer.Fire);
        }

        private sealed class CapturedTimer(TimerCallback callback, CancellationTokenSource deadline) : ITimer
        {
            private int _disposed;
            public CancellationTokenSource Deadline { get; } = deadline;
            public void Fire() => callback(Deadline);
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

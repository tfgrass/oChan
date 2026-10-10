using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using oChan.Downloader;

namespace oChan.Tests;

public sealed class BandwidthLimiterTests
{
    [Fact]
    public async Task AChunkLargerThanOneSecondPaysItsFullBudgetWithoutIdleCredit()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(1024, clock);
        clock.Advance(TimeSpan.FromHours(1));

        Task transfer = limiter.ThrottleAsync(81920, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(80), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(79));
        Assert.False(transfer.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await transfer.WaitAsync(TestWait.Timeout);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task ConcurrentTransfersShareOneBudget()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(1024, clock);
        Task first = limiter.ThrottleAsync(1024, CancellationToken.None);
        Task second = limiter.ThrottleAsync(1024, CancellationToken.None);
        Task third = limiter.ThrottleAsync(1024, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.WaitAsync(TestWait.Timeout);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TestWait.Timeout);
        Assert.False(third.IsCompleted);

        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        await third.WaitAsync(TestWait.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, clock.GetTimestamp());
    }

    [Theory]
    [InlineData(20, 4)]
    [InlineData(10, 8)]
    [InlineData(5, 16)]
    public async Task UpdatingTheRateWakesTheWaitAndPreservesUnpaidBytes(long newRate, int remainingSeconds)
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(10, clock);
        Task transfer = limiter.ThrottleAsync(100, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(10), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(2));

        limiter.UpdateMaxBytesPerSecond(newRate);
        Assert.Equal(newRate, limiter.MaxBytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(remainingSeconds), await clock.NextTimerAsync());
        Assert.Equal(1, clock.ActiveTimers);
        clock.Advance(TimeSpan.FromSeconds(remainingSeconds - 1));
        Assert.False(transfer.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await transfer.WaitAsync(TestWait.Timeout);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task CancellingAnActiveWaitKeepsAlreadyChargedDebt()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(10, clock);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task cancelled = limiter.ThrottleAsync(100, cancellation.Token);
        await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, clock.ActiveTimers);

        // 80 bytes from the cancelled transfer and 20 new bytes still need budget.
        Task following = limiter.ThrottleAsync(20, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(10), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(10));
        await following.WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task CancellingAQueuedCallerDoesNotChargeAnUnstartedChunk()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(10, clock);
        Task active = limiter.ThrottleAsync(100, CancellationToken.None);
        await clock.NextTimerAsync();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task queued = limiter.ThrottleAsync(100, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, clock.ActiveTimers);

        clock.Advance(TimeSpan.FromSeconds(10));
        await active.WaitAsync(TestWait.Timeout);
        Task following = limiter.ThrottleAsync(10, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        await following.WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task ExtremeRatesAndLongElapsedTimeDoNotOverflowAccounting()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(long.MaxValue, clock);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task cancelled = limiter.ThrottleAsync(int.MaxValue, cancellation.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(1), await clock.NextTimerAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        clock.Advance(TimeSpan.FromDays(1000000));
        limiter.UpdateMaxBytesPerSecond(long.MaxValue);
        Task transfer = limiter.ThrottleAsync(int.MaxValue, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(1), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await transfer.WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task VeryLongDebtUsesAValidCancellableTimer()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        BandwidthLimiter limiter = new BandwidthLimiter(1, clock);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task transfer = limiter.ThrottleAsync(int.MaxValue, cancellation.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1L), await clock.NextTimerAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task InvalidInputsAreRejectedAndZeroBytesNeedNoTimer()
    {
        DeterministicTimeProvider clock = new DeterministicTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(0, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(-1, clock));
        BandwidthLimiter limiter = new BandwidthLimiter(10, clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.UpdateMaxBytesPerSecond(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.UpdateMaxBytesPerSecond(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => limiter.ThrottleAsync(-1, CancellationToken.None));
        await limiter.ThrottleAsync(0, CancellationToken.None);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(10, limiter.MaxBytesPerSecond);
    }

    private sealed class DeterministicTimeProvider : TimeProvider
    {
        private readonly object _stateLock = new object();
        private readonly List<ManualTimer> _timers = new List<ManualTimer>();
        private readonly Channel<TimeSpan> _createdTimers = Channel.CreateUnbounded<TimeSpan>();
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public int ActiveTimers
        {
            get
            {
                lock (_stateLock)
                {
                    int active = 0;
                    foreach (ManualTimer timer in _timers)
                    {
                        if (timer.DueAt != null)
                        {
                            active++;
                        }
                    }

                    return active;
                }
            }
        }

        public override long GetTimestamp()
        {
            lock (_stateLock)
            {
                return _timestamp;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new ManualTimer(this, callback, state);
            lock (_stateLock)
            {
                _timers.Add(timer);
                ChangeLocked(timer, dueTime, period);
            }

            _createdTimers.Writer.TryWrite(dueTime);
            return timer;
        }

        public Task<TimeSpan> NextTimerAsync()
        {
            return _createdTimers.Reader.ReadAsync().AsTask().WaitAsync(TestWait.Timeout);
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            long target;
            lock (_stateLock)
            {
                target = checked(_timestamp + elapsed.Ticks);
            }

            while (true)
            {
                ManualTimer? next = null;
                lock (_stateLock)
                {
                    foreach (ManualTimer timer in _timers)
                    {
                        if (timer.DueAt != null && timer.DueAt <= target &&
                            (next == null || timer.DueAt < next.DueAt))
                        {
                            next = timer;
                        }
                    }

                    if (next == null)
                    {
                        _timestamp = target;
                        return;
                    }

                    _timestamp = next.DueAt!.Value;
                    next.DueAt = next.Period > TimeSpan.Zero ? checked(_timestamp + next.Period.Ticks) : null;
                }

                next.Callback(next.State);
            }
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_stateLock)
            {
                if (timer.IsDisposed)
                {
                    return false;
                }

                ChangeLocked(timer, dueTime, period);
            }

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _createdTimers.Writer.TryWrite(dueTime);
            }

            return true;
        }

        private void ChangeLocked(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            if ((dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) ||
                (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan))
            {
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            }

            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : checked(_timestamp + dueTime.Ticks);
            timer.Period = period;
        }

        private void Dispose(ManualTimer timer)
        {
            lock (_stateLock)
            {
                timer.IsDisposed = true;
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly DeterministicTimeProvider _owner;
            public TimerCallback Callback { get; }
            public object? State { get; }
            public long? DueAt { get; set; }
            public TimeSpan Period { get; set; }
            public bool IsDisposed { get; set; }

            public ManualTimer(DeterministicTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                Callback = callback;
                State = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => _owner.Change(this, dueTime, period);
            public void Dispose() => _owner.Dispose(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

namespace oChan.Downloader;

using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

/// <summary>
/// Shares a byte budget across transfers. There is no accumulated idle credit:
/// each chunk waits for its complete budget before it is released to the caller.
/// </summary>
public class BandwidthLimiter
{
    // Task.Delay's default timer supports at most this interval. Longer debts are
    // paid in several waits rather than overflowing a duration or timer argument.
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);
    private readonly SemaphoreSlim _turn = new SemaphoreSlim(1, 1);
    private readonly object _budgetLock = new object();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private TaskCompletionSource _budgetChanged = CreateCompletionSource();
    private long _maxBytesPerSecond;
    private long _accountedAt;
    private decimal _outstandingBytes;

    public long MaxBytesPerSecond => Interlocked.Read(ref _maxBytesPerSecond);

    public BandwidthLimiter(long maxBytesPerSecond, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        if (maxBytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytesPerSecond), "Max bytes per second must be greater than zero.");
        }

        _maxBytesPerSecond = maxBytesPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Serilog.Core.Logger.None;
        _accountedAt = _timeProvider.GetTimestamp();
    }

    public void UpdateMaxBytesPerSecond(long newMax)
    {
        if (newMax <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newMax), "Max bytes per second must be greater than zero.");
        }

        TaskCompletionSource changed;
        long previousMax;
        lock (_budgetLock)
        {
            AccountElapsedBudgetLocked();
            previousMax = Interlocked.Exchange(ref _maxBytesPerSecond, newMax);
            changed = _budgetChanged;
            _budgetChanged = CreateCompletionSource();
        }

        changed.TrySetResult();
        _logger.Debug("Updated bandwidth from {OldMax} to {NewMax} bytes per second", previousMax, newMax);
    }

    public async Task ThrottleAsync(int bytesDownloaded, CancellationToken cancellationToken)
    {
        if (bytesDownloaded < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesDownloaded));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bytesDownloaded == 0)
        {
            return;
        }

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_budgetLock)
            {
                AccountElapsedBudgetLocked();
                _outstandingBytes += bytesDownloaded;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan delay;
                Task changed;
                lock (_budgetLock)
                {
                    AccountElapsedBudgetLocked();
                    if (_outstandingBytes == 0)
                    {
                        return;
                    }

                    delay = GetDelayLocked();
                    changed = _budgetChanged.Task;
                }

                using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task elapsed = Task.Delay(delay, _timeProvider, waitCancellation.Token);
                Task completed = await Task.WhenAny(elapsed, changed).ConfigureAwait(false);
                // Cancel the obsolete timer on a settings change, so repeated
                // updates cannot leave an increasing number of scheduled waits.
                await waitCancellation.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(completed, elapsed))
                {
                    await elapsed.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Bytes are already read by the caller. Cancelling its wait therefore
            // leaves unpaid debt for the following transfer instead of resetting it.
            _turn.Release();
        }
    }

    private void AccountElapsedBudgetLocked()
    {
        long now = _timeProvider.GetTimestamp();
        TimeSpan elapsed = _timeProvider.GetElapsedTime(_accountedAt, now);
        _accountedAt = now;
        if (_outstandingBytes == 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        decimal seconds = (decimal)elapsed.Ticks / TimeSpan.TicksPerSecond;
        // Compare before multiplying, including at long.MaxValue rates and after
        // long idle periods, to keep the accounting arithmetic within its range.
        if (seconds >= _outstandingBytes / _maxBytesPerSecond)
        {
            _outstandingBytes = 0;
        }
        else
        {
            _outstandingBytes -= seconds * _maxBytesPerSecond;
        }
    }

    private TimeSpan GetDelayLocked()
    {
        decimal seconds = _outstandingBytes / _maxBytesPerSecond;
        decimal maximumSeconds = (decimal)MaximumDelay.Ticks / TimeSpan.TicksPerSecond;
        if (seconds >= maximumSeconds)
        {
            return MaximumDelay;
        }

        long ticks = (long)decimal.Ceiling(seconds * TimeSpan.TicksPerSecond);
        // System timers have millisecond granularity. A sub-millisecond debt must
        // not turn into a zero-duration Task.Delay and a busy loop.
        return TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, ticks));
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

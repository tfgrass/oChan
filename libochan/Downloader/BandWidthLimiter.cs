using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace oChan.Downloader
{
    public class BandwidthLimiter
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private long _maxBytesPerSecond;
        private long _bytesDownloadedThisSecond;
        private DateTime _currentSecondStart;

        public BandwidthLimiter(long maxBytesPerSecond)
        {
            if (maxBytesPerSecond <= 0)
            {
                Log.Error("Attempted to initialize BandwidthLimiter with non-positive maxBytesPerSecond: {MaxBytesPerSecond}", maxBytesPerSecond);
                throw new ArgumentException("Max bytes per second must be greater than zero.");
            }

            _maxBytesPerSecond = maxBytesPerSecond;
            _currentSecondStart = DateTime.UtcNow;

            Log.Information("Initialized BandwidthLimiter with maxBytesPerSecond: {MaxBytesPerSecond}", _maxBytesPerSecond);
        }

        public void UpdateMaxBytesPerSecond(long newMax)
        {
            if (newMax <= 0)
            {
                Log.Error("Attempted to update maxBytesPerSecond to non-positive value: {NewMax}", newMax);
                throw new ArgumentException("Max bytes per second must be greater than zero.");
            }

            Log.Information("Updating maxBytesPerSecond from {OldMax} to {NewMax}", _maxBytesPerSecond, newMax);
            _maxBytesPerSecond = newMax;
        }

        public async Task ThrottleAsync(int bytesDownloaded, CancellationToken cancellationToken)
        {
            Log.Debug("ThrottleAsync called with bytesDownloaded: {BytesDownloaded}", bytesDownloaded);

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;

                if ((now - _currentSecondStart).TotalSeconds >= 1)
                {
                    Log.Debug("One second has passed. Resetting counters.");
                    // Reset the counter every second
                    _currentSecondStart = now;
                    _bytesDownloadedThisSecond = 0;
                }

                _bytesDownloadedThisSecond += bytesDownloaded;
                Log.Debug("Updated bytesDownloadedThisSecond: {BytesDownloadedThisSecond}", _bytesDownloadedThisSecond);

                if (_bytesDownloadedThisSecond > _maxBytesPerSecond)
                {
                    // Calculate delay
                    var delay = 1000 - (now - _currentSecondStart).TotalMilliseconds;
                    if (delay > 0)
                    {
                        Log.Debug("Throttling download. Sleeping for {Delay} milliseconds.", delay);
                        await Task.Delay((int)delay, cancellationToken);
                    }
                    else
                    {
                        Log.Debug("No delay required. Continuing without delay.");
                    }
                    // Reset counters after delay
                    _currentSecondStart = DateTime.UtcNow;
                    _bytesDownloadedThisSecond = 0;
                }
            }
            finally
            {
                _semaphore.Release();
                Log.Debug("Semaphore released in ThrottleAsync.");
            }
        }
    }
}

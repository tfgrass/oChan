namespace oChan.Downloader;


using System;
using System.Threading;
using System.Threading.Tasks;
public class BandwidthLimiter
{
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private long _maxBytesPerSecond;
    private long _bytesDownloadedThisSecond;
    private DateTime _currentSecondStart;

    public BandwidthLimiter(long maxBytesPerSecond)
    {
        _maxBytesPerSecond = maxBytesPerSecond;
        _currentSecondStart = DateTime.UtcNow;
    }

    public void UpdateMaxBytesPerSecond(long newMax)
    {
        if (newMax <= 0) throw new ArgumentException("Max bytes per second must be greater than zero.");
        _maxBytesPerSecond = newMax;
    }

    public async Task ThrottleAsync(int bytesDownloaded, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;

            if ((now - _currentSecondStart).TotalSeconds >= 1)
            {
                // Reset the counter every second
                _currentSecondStart = now;
                _bytesDownloadedThisSecond = 0;
            }

            _bytesDownloadedThisSecond += bytesDownloaded;

            if (_bytesDownloadedThisSecond > _maxBytesPerSecond)
            {
                // Calculate delay
                var delay = 1000 - (now - _currentSecondStart).TotalMilliseconds;
                if (delay > 0)
                {
                    await Task.Delay((int)delay, cancellationToken);
                }
                // Reset counters after delay
                _currentSecondStart = DateTime.UtcNow;
                _bytesDownloadedThisSecond = 0;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

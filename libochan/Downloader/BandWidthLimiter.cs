namespace oChan.Downloader;

using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

public class BandwidthLimiter
{
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private long _maxBytesPerSecond;
    private long _bytesDownloadedThisSecond;
    private DateTime _currentSecondStart;

    private readonly Config _config; // Reference to the config

    public BandwidthLimiter()
    {
        _config = Config.LoadConfig(); // Load the configuration

        if (_config.BandwidthLimiter <= 0)
        {
            Log.Error("Attempted to initialize BandwidthLimiter with non-positive maxBytesPerSecond from config: {MaxBytesPerSecond}", _config.BandwidthLimiter);
            throw new ArgumentException("Max bytes per second from configuration must be greater than zero.");
        }

        _maxBytesPerSecond = _config.BandwidthLimiter;
        _currentSecondStart = DateTime.UtcNow;

        Log.Information("Initialized BandwidthLimiter with maxBytesPerSecond from config: {MaxBytesPerSecond} ({HumanReadableMaxBytesPerSecond})", 
            _maxBytesPerSecond, Utils.ToHumanReadableSize(_maxBytesPerSecond));
    }

    public void UpdateMaxBytesPerSecond(long newMax)
    {
        if (newMax <= 0)
        {
            Log.Error("Attempted to update maxBytesPerSecond to non-positive value: {NewMax}", newMax);
            throw new ArgumentException("Max bytes per second must be greater than zero.");
        }

        Log.Information("Updating maxBytesPerSecond from {OldMax} ({HumanReadableOldMax}) to {NewMax} ({HumanReadableNewMax})",
            _maxBytesPerSecond, Utils.ToHumanReadableSize(_maxBytesPerSecond), newMax, Utils.ToHumanReadableSize(newMax));
        _maxBytesPerSecond = newMax;
    }

    public async Task ThrottleAsync(int bytesDownloaded, CancellationToken cancellationToken)
    {
        Log.Verbose("ThrottleAsync called with bytesDownloaded: {BytesDownloaded} ({HumanReadableBytesDownloaded})", bytesDownloaded, Utils.ToHumanReadableSize(bytesDownloaded));

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            DateTime now = DateTime.UtcNow;

            if ((now - _currentSecondStart).TotalSeconds >= 1)
            {
                Log.Debug("One second has passed. Resetting counters.");
                // Reset the counter every second
                _currentSecondStart = now;
                _bytesDownloadedThisSecond = 0;
            }

            _bytesDownloadedThisSecond += bytesDownloaded;
            Log.Verbose("Updated bytesDownloadedThisSecond: {BytesDownloadedThisSecond} ({HumanReadableBytesDownloadedThisSecond})",
                _bytesDownloadedThisSecond, Utils.ToHumanReadableSize(_bytesDownloadedThisSecond));

            if (_bytesDownloadedThisSecond > _maxBytesPerSecond)
            {
                // Calculate delay
                int delay = (int)(1000 - (now - _currentSecondStart).TotalMilliseconds);
                if (delay > 0)
                {
                    Log.Debug("Throttling download. Sleeping for {Delay} milliseconds.", delay);
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    Log.Verbose("No delay required. Continuing without delay.");
                }
                // Reset counters after delay
                _currentSecondStart = DateTime.UtcNow;
                _bytesDownloadedThisSecond = 0;
            }
        }
        finally
        {
            _semaphore.Release();
            Log.Verbose("Semaphore released in ThrottleAsync.");
        }
    }
}

namespace oChan;

using System;
using System.IO;

public sealed class SessionOptions
{
    public string DownloadPath { get; init; } = string.Empty;
    public TimeSpan RecheckInterval { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxParallelDownloads { get; init; } = 5;
    public long BandwidthBytesPerSecond { get; init; } = 1024 * 1024;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DownloadPath);
        _ = Path.GetFullPath(DownloadPath);
        if (RecheckInterval < TimeSpan.FromSeconds(10) || RecheckInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(RecheckInterval), "The recheck interval must be between 10 seconds and the supported timer maximum.");
        if (MaxParallelDownloads <= 0) throw new ArgumentOutOfRangeException(nameof(MaxParallelDownloads));
        if (BandwidthBytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(BandwidthBytesPerSecond));
    }
}

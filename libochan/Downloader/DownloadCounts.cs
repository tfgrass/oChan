namespace oChan.Downloader;

/// <summary>Waiting media jobs and jobs occupying a worker slot, for one watch instance.</summary>
public readonly record struct DownloadCounts(int Queued, int Active);

namespace oChan.Downloader;

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;


public class DownloadWorker
{
    private readonly DownloadItem _downloadItem;
    private readonly BandwidthLimiter _bandwidthLimiter;

    public DownloadWorker(DownloadItem downloadItem, BandwidthLimiter bandwidthLimiter)
    {
        if (downloadItem == null)
        {
            Log.Error("DownloadItem is null in DownloadWorker constructor.");
            throw new ArgumentNullException(nameof(downloadItem));
        }

        if (bandwidthLimiter == null)
        {
            Log.Error("BandwidthLimiter is null in DownloadWorker constructor.");
            throw new ArgumentNullException(nameof(bandwidthLimiter));
        }

        _downloadItem = downloadItem;
        _bandwidthLimiter = bandwidthLimiter;

        Log.Debug("Created DownloadWorker for {DownloadUri}", _downloadItem.DownloadUri);
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_downloadItem.Thread.IsMediaDownloaded(_downloadItem.MediaIdentifier))
            {
                Log.Information("Media {MediaIdentifier} already downloaded for thread {ThreadId}, skipping download.",
                    _downloadItem.MediaIdentifier, _downloadItem.Thread.ThreadId);
                return;
            }

            Log.Debug("Starting execution of DownloadWorker for {DownloadUri}", _downloadItem.DownloadUri);

            HttpClient httpClient = _downloadItem.ImageBoard.GetHttpClient();

            using HttpResponseMessage response = await httpClient.GetAsync(_downloadItem.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string directory = Path.GetDirectoryName(_downloadItem?.DestinationPath) ?? string.Empty;
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Log.Debug("Creating directory {Directory}", directory);
                Directory.CreateDirectory(directory);
            }

            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using FileStream fileStream = new FileStream(_downloadItem?.DestinationPath ?? throw new ArgumentNullException(nameof(_downloadItem.DestinationPath)), FileMode.Create, FileAccess.Write, FileShare.None);

            Log.Debug("Beginning file copy to {DestinationPath}", _downloadItem.DestinationPath);

            long totalBytes = await CopyToAsync(contentStream, fileStream, 81920, cancellationToken);

            Log.Information("Successfully downloaded {DownloadUri} to {DestinationPath}. File size: {TotalBytes} bytes ({HumanReadableTotalBytes})",
                _downloadItem.DownloadUri, _downloadItem.DestinationPath, totalBytes, Utils.ToHumanReadableSize(totalBytes));

            _downloadItem.Thread.MarkMediaAsDownloaded(_downloadItem.MediaIdentifier);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing download for {DownloadUri}: {Message}", _downloadItem.DownloadUri, ex.Message);
            throw;
        }
    }

    private async Task<long> CopyToAsync(Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[bufferSize];
        int bytesRead;
        long totalBytes = 0;

        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken)) > 0)
        {
            await _bandwidthLimiter.ThrottleAsync(bytesRead, cancellationToken);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

            totalBytes += bytesRead; // Accumulate total bytes
        }

        Log.Debug("Completed file copy. Total bytes copied: {TotalBytes} bytes", totalBytes);

        return totalBytes; // Return total bytes for logging in ExecuteAsync
    }
}


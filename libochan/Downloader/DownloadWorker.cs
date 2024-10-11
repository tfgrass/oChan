using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace oChan.Downloader
{
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
                Log.Debug("Starting execution of DownloadWorker for {DownloadUri}", _downloadItem.DownloadUri);

                var httpClient = _downloadItem.ImageBoard.GetHttpClient();

                using var response = await httpClient.GetAsync(_downloadItem.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var directory = Path.GetDirectoryName(_downloadItem.DestinationPath);
                if (!Directory.Exists(directory))
                {
                    Log.Debug("Creating directory {Directory}", directory);
                    Directory.CreateDirectory(directory);
                }

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(_downloadItem.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

                Log.Debug("Beginning file copy to {DestinationPath}", _downloadItem.DestinationPath);

                await CopyToAsync(contentStream, fileStream, 81920, cancellationToken);

                Log.Information("Successfully downloaded {DownloadUri} to {DestinationPath}", _downloadItem.DownloadUri, _downloadItem.DestinationPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing download for {DownloadUri}: {Message}", _downloadItem.DownloadUri, ex.Message);
                throw; // Rethrow to allow calling code to handle exception
            }
        }

        private async Task CopyToAsync(Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            var buffer = new byte[bufferSize];
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken)) > 0)
            {
                await _bandwidthLimiter.ThrottleAsync(bytesRead, cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                totalBytes += bytesRead;
            }

            Log.Debug("Completed file copy of {TotalBytes} bytes to {DestinationPath}", totalBytes, _downloadItem.DestinationPath);
        }
    }
}

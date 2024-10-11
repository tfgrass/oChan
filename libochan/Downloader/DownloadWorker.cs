namespace oChan.Downloader;

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using oChan.Interfaces;

public class DownloadWorker
{
    private readonly DownloadItem _downloadItem;
    private readonly BandwidthLimiter _bandwidthLimiter;

    public DownloadWorker(DownloadItem downloadItem, BandwidthLimiter bandwidthLimiter)
    {
        _downloadItem = downloadItem ?? throw new ArgumentNullException(nameof(downloadItem));
        _bandwidthLimiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var httpClient = _downloadItem.ImageBoard.GetHttpClient();

        using var response = await httpClient.GetAsync(_downloadItem.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var directory = Path.GetDirectoryName(_downloadItem.DestinationPath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(_downloadItem.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        await CopyToAsync(contentStream, fileStream, 81920, cancellationToken);
    }

    private async Task CopyToAsync(Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        var buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await _bandwidthLimiter.ThrottleAsync(bytesRead, cancellationToken);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }
}


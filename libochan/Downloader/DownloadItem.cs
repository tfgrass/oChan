namespace oChan.Downloader;

using System;
using oChan.Interfaces;
using Serilog;

public class DownloadItem
{
    public Uri DownloadUri { get; }
    public string DestinationPath { get; }
    public IImageBoard ImageBoard { get; }
    public IThread Thread { get; }
    public string MediaIdentifier { get; }

    public DownloadItem(Uri downloadUri, string destinationPath, IImageBoard imageBoard, IThread thread, string mediaIdentifier)
    {
        if (downloadUri == null)
        {
            Log.Error("DownloadUri is null in DownloadItem constructor.");
            throw new ArgumentNullException(nameof(downloadUri));
        }

        if (string.IsNullOrEmpty(destinationPath))
        {
            Log.Error("DestinationPath is null or empty in DownloadItem constructor.");
            throw new ArgumentNullException(nameof(destinationPath));
        }

        if (imageBoard == null)
        {
            Log.Error("ImageBoard is null in DownloadItem constructor.");
            throw new ArgumentNullException(nameof(imageBoard));
        }

        if (thread == null)
        {
            Log.Error("Thread is null in DownloadItem constructor.");
            throw new ArgumentNullException(nameof(thread));
        }

        if (string.IsNullOrEmpty(mediaIdentifier))
        {
            Log.Error("MediaIdentifier is null or empty in DownloadItem constructor.");
            throw new ArgumentNullException(nameof(mediaIdentifier));
        }

        // Ensure that the path is properly formatted and sanitized
        DestinationPath = PathSanitizer.SanitizePath(destinationPath);

        DownloadUri = downloadUri;
        ImageBoard = imageBoard;
        Thread = thread;
        MediaIdentifier = mediaIdentifier;

        Log.Information("Created DownloadItem for URI: {DownloadUri}, Destination: {DestinationPath}", DownloadUri, DestinationPath);
    }
}

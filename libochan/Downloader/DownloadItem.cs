namespace oChan.Downloader;

using System;
using System.IO;
using oChan.Interfaces;

public class DownloadItem
{
    public Uri DownloadUri { get; }
    public string DestinationPath { get; }
    public IImageBoard ImageBoard { get; }
    public IThread Thread { get; }
    public string MediaIdentifier { get; }
    public long? ExpectedSize { get; }
    public string? ExpectedMd5 { get; }
    public string? OriginalFileName { get; }
    public string? LegacyPath { get; }

    public DownloadItem(Uri downloadUri, string destinationPath, IImageBoard imageBoard, IThread thread, string mediaIdentifier,
        long? expectedSize = null, string? expectedMd5 = null, string? originalFileName = null, string? legacyPath = null)
    {
        if (downloadUri == null)
        {
            throw new ArgumentNullException(nameof(downloadUri));
        }

        if (string.IsNullOrEmpty(destinationPath))
        {
            throw new ArgumentNullException(nameof(destinationPath));
        }

        if (imageBoard == null)
        {
            throw new ArgumentNullException(nameof(imageBoard));
        }

        if (thread == null)
        {
            throw new ArgumentNullException(nameof(thread));
        }

        if (string.IsNullOrEmpty(mediaIdentifier))
        {
            throw new ArgumentNullException(nameof(mediaIdentifier));
        }

        DestinationPath = Path.GetFullPath(destinationPath);

        DownloadUri = downloadUri;
        ImageBoard = imageBoard;
        Thread = thread;
        MediaIdentifier = mediaIdentifier;
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        ExpectedSize = expectedSize;
        ExpectedMd5 = expectedMd5;
        OriginalFileName = originalFileName;
        LegacyPath = legacyPath;

    }
}

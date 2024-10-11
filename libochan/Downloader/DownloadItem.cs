namespace oChan.Downloader;

using System;
using oChan.Interfaces;
public class DownloadItem
{
    public Uri DownloadUri { get; }
    public string DestinationPath { get; }
    public IImageBoard ImageBoard { get; }

    public DownloadItem(Uri downloadUri, string destinationPath, IImageBoard imageBoard)
    {
        DownloadUri = downloadUri ?? throw new ArgumentNullException(nameof(downloadUri));
        DestinationPath = destinationPath ?? throw new ArgumentNullException(nameof(destinationPath));
        ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
    }
}
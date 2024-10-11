using System;
using oChan.Interfaces;
using Serilog;

namespace oChan.Downloader
{
    public class DownloadItem
    {
        public Uri DownloadUri { get; }
        public string DestinationPath { get; }
        public IImageBoard ImageBoard { get; }

        public DownloadItem(Uri downloadUri, string destinationPath, IImageBoard imageBoard)
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

            DownloadUri = downloadUri;
            DestinationPath = destinationPath;
            ImageBoard = imageBoard;

            Log.Information("Created DownloadItem for URI: {DownloadUri}, Destination: {DestinationPath}", DownloadUri, DestinationPath);
        }
    }
}

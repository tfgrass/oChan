using System;
using System.Collections.Generic;
using oChan.Downloaders;
namespace oChan
{
    public class Registry
    {
        private List<Downloader> _registeredDownloaders = new List<Downloader>();

        // Register a new downloader in the registry
        public void RegisterDownloader(Downloader downloader)
        {
            _registeredDownloaders.Add(downloader);
        }

        // Check if any registered downloader can handle the given URL and start the download
        public Downloader HandleUrl(string url)
        {
            foreach (var downloader in _registeredDownloaders)
            {
                if (downloader.CanHandle(url))
                {
                    downloader.Url = url;
                    downloader.StartDownload();
                    return downloader;
                }
            }
            Console.WriteLine($"No downloader found for URL: {url}");
            return null;
        }
    }
}

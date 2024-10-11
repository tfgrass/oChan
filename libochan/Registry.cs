using System;
using System.Collections.Generic;
using Serilog;
using oChan.Boards;

namespace oChan
{
    public class Registry
    {
        // Store singleton instances of downloaders in a dictionary
        private Dictionary<Type, Boards.Downloader> _registeredDownloaders = new();

        public Registry()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Set the minimum log level
                .WriteTo.Console()    // Output logs to the console
                .CreateLogger();
        }

        // Register a new downloader in the registry (by instance)
        public void RegisterDownloader(Boards.Downloader downloader)
        {
            var downloaderType = downloader.GetType();

            // Ensure the downloader is only registered once
            if (!_registeredDownloaders.ContainsKey(downloaderType))
            {
                _registeredDownloaders[downloaderType] = downloader;
                Console.WriteLine($"Registered {downloaderType.Name}.");
            }
        }

        // Find a downloader that can handle the URL and enqueue the download
        public Boards.Downloader? HandleUrl(string url)
        {
            foreach (var downloader in _registeredDownloaders.Values)
            {
                if (downloader.CanHandle(url))
                {
                    downloader.QueueDownload(url); // Use the queuing method from the downloader
                    return downloader;
                }
            }

            Console.WriteLine($"No downloader found for URL: {url}");
            return null;
        }

        // List all registered downloaders
        public void ListDownloaders()
        {
            foreach (var downloaderType in _registeredDownloaders.Keys)
            {
                Console.WriteLine($"Registered downloader: {downloaderType.Name}");
            }
        }
    }
}

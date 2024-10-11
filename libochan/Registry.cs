using System;
using System.Collections.Generic;
using Serilog;
using oChan.Boards;

namespace oChan
{
    public class Registry
    {
        // Store singleton instances of downloaders in a dictionary
        private readonly Dictionary<Type, Boards.Downloader> _registeredDownloaders = new();

        public Registry()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Set the minimum log level
                .WriteTo.Console()    // Output logs to the console
                .CreateLogger();

            Log.Information("Registry initialized with logging configured.");
        }

        // Register a new downloader in the registry (by instance)
        public void RegisterDownloader(Boards.Downloader downloader)
        {
            if (downloader == null)
            {
                Log.Error("Attempted to register a null downloader.");
                throw new ArgumentNullException(nameof(downloader));
            }

            var downloaderType = downloader.GetType();

            // Ensure the downloader is only registered once
            if (!_registeredDownloaders.ContainsKey(downloaderType))
            {
                _registeredDownloaders[downloaderType] = downloader;
                Log.Information("Registered downloader: {DownloaderName}", downloaderType.Name);
            }
            else
            {
                Log.Warning("Downloader {DownloaderName} is already registered.", downloaderType.Name);
            }
        }

        // Find a downloader that can handle the URL and enqueue the download
        public Boards.Downloader? HandleUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Log.Error("URL is null or empty.");
                throw new ArgumentNullException(nameof(url));
            }

            foreach (var downloader in _registeredDownloaders.Values)
            {
                if (downloader.CanHandle(url))
                {
                    Log.Debug("Downloader {DownloaderName} can handle URL: {Url}", downloader.GetType().Name, url);
                    downloader.QueueDownload(url); // Use the queuing method from the downloader
                    return downloader;
                }
            }

            Log.Warning("No downloader found for URL: {Url}", url);
            return null;
        }

        // List all registered downloaders
        public void ListDownloaders()
        {
            if (_registeredDownloaders.Count == 0)
            {
                Log.Information("No downloaders registered.");
            }
            else
            {
                foreach (var downloaderType in _registeredDownloaders.Keys)
                {
                    Log.Information("Registered downloader: {DownloaderName}", downloaderType.Name);
                }
            }
        }
    }
}

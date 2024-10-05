using System;
using System.Threading.Tasks;
using oChan;

namespace oChan.Downloaders
{
    public class FourChanDownloader : Downloader
    {
        private readonly Config _config;

        // Constructor to automatically fetch the config
        public FourChanDownloader()
        {
            _config = new Config();  // Initialize the configuration internally
            _config.PrintConfig();   // For debugging: Print the download path
        }

        // Override the method from the base class
        public override bool CanHandle(string url)
        {
            return url.Contains("https://boards.4chan.org/");
        }

        // Override the async download method from the base class
        public override async Task DownloadThreadAsync(string url)
        {
            // Use the download path from the configuration
            string downloadPath = _config.DownloadPath;
            Console.WriteLine($"Download Path: {downloadPath}");

            updateProps(url, "0/0", "Idle");
            string status = "Downloading";
            
            // Simulate download steps (later you'll save files to the path)
            await Task.Delay(2000); // Simulate delay for downloading
            updateProps(url, "10000/10", status);

            await Task.Delay(2000);
            updateProps(url, "2/10", status);

            await Task.Delay(2000);
            updateProps(url, "3/10", status);

            await Task.Delay(2000);
            updateProps(url, "4/10", status);

            await Task.Delay(2000);
            updateProps(url, "5/10", status);

            Status = "Completed";
        }

        private void updateProps(string url, string progress, string status)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Url = url;
                Progress = progress;
                Status = status;
                Console.WriteLine($"URL: {url}, Progress: {progress}, Status: {status}");
            });
        }
    }
}

using System;
using System.Threading.Tasks;

namespace oChan.Downloaders
{
    public class FourChanDownloader : Downloader
    {
        // Override the method from the base class
        public override bool CanHandle(string url)
        {
            return url.Contains("https://boards.4chan.org/");
        }

        // Override the async download method from the base class
        public override async Task DownloadThreadAsync(string url)
        {
            updateProps(url, "0/0", "Idle");
            string status = "Downloading";
            
            await Task.Delay(2000); // Use Task.Delay for async, non-blocking delay
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

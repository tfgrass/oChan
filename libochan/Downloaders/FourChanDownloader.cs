using System;

namespace oChan.Downloaders
{
    public class FourChanDownloader : Downloader
    {
        // Check if the URL can be handled by this downloader (e.g., URL starts with "https://boards.4chan.org/")
        public override bool CanHandle(string url)
        {
            return url.Contains("boards.4chan.org");
        }

        // Start the download process (you will implement the actual downloading logic here)
        public override void StartDownload()
        {
            Status = "Downloading";
            Console.WriteLine($"Starting download for 4chan URL: {Url}");
        }
    }
}

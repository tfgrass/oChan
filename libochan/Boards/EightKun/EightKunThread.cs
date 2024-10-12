using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using oChan.Boards.Base;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;
using  System.Text.RegularExpressions;
namespace oChan.Boards.EightKun
{
    public class EightKunThread : BaseThread
    {
        public override IBoard Board { get; }
        public override string ThreadId { get; }
        public override string Title { get; set; }
        public override string NiceName => $"{Title} ({ThreadId})";
        public override Uri ThreadUri { get; }

        public EightKunThread(IBoard board, Uri threadUri)
        {
            Board = board ?? throw new ArgumentNullException(nameof(board));
            ThreadUri = threadUri ?? throw new ArgumentNullException(nameof(threadUri));

            ThreadId = ExtractThreadId(threadUri);
            Title = "Unknown";
            Status = "Pending";
            Log.Information("Initialized EightKunThread with ID: {ThreadId}", ThreadId);
        }

        public override async Task RecheckThreadAsync(DownloadQueue queue)
        {
            Status = "Rechecking"; // Update the status to "Rechecking"
            await base.RecheckThreadAsync(queue);

            try
            {
                Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);

                HttpClient client = Board.ImageBoard.GetHttpClient();
                HttpResponseMessage response = await client.GetAsync(ThreadUri);
                response.EnsureSuccessStatusCode();
                string htmlContent = await response.Content.ReadAsStringAsync();

                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                HtmlNodeCollection imageNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'file')]//a[contains(@href, 'media')]");

                // Preserve the previous counts
                int previousTotalMediaCount = TotalMediaCount;
                int newMediaCount = 0;

                HashSet<string> uniqueImageUrls = new HashSet<string>();

                if (imageNodes != null)
                {
                    foreach (HtmlNode node in imageNodes)
                    {
                        string imageUrl = node.GetAttributeValue("href", string.Empty);
                        string mediaIdentifier = ExtractMediaIdentifier(imageUrl);

                        // Skip already downloaded media
                        if (!IsMediaDownloaded(mediaIdentifier) && !uniqueImageUrls.Contains(imageUrl))
                        {
                            string fileName = node.InnerText.Trim();
                            string destinationPath = Path.Combine("Downloads", Board.BoardCode, ThreadId, fileName);

                            DownloadItem downloadItem = new DownloadItem(new Uri(imageUrl), destinationPath, Board.ImageBoard, this, mediaIdentifier);
                            queue.EnqueueDownload(downloadItem);
                            Log.Debug("Enqueued download for image {ImageUrl}", imageUrl);
                            uniqueImageUrls.Add(imageUrl);
                            newMediaCount++; // Increment new media count
                        }
                        else
                        {
                            Log.Debug("Skipping duplicate or already downloaded media {MediaIdentifier} for thread {ThreadId}", mediaIdentifier, ThreadId);
                        }
                    }

                    // Update the total media count only if there are new items
                    if (newMediaCount > 0)
                    {
                        TotalMediaCount = previousTotalMediaCount + newMediaCount;
                        Status = "Downloading"; // Only set to "Downloading" if new media is enqueued
                    }
                }
                else
                {
                    Log.Warning("No images found in thread {ThreadId}", ThreadId);
                }

                // Retain the previous media count if no new images are found
                if (newMediaCount == 0)
                {
                    TotalMediaCount = previousTotalMediaCount; // Keep the previous total media count if no new media is found
                }

                Log.Information("Recheck complete for thread {ThreadId}: Enqueued {NewMediaCount} new media downloads", ThreadId, newMediaCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error enqueuing media downloads for thread {ThreadId}: {Message}", ThreadId, ex.Message);
            }

            // If all media has been downloaded, set the status to "Finished"
            if (DownloadedMediaCount == TotalMediaCount && TotalMediaCount > 0)
            {
                Status = "Finished"; // Set status to "Finished" after recheck and download completion
            }
            else if (TotalMediaCount == 0)
            {
                Status = "No media found"; // Handle case where no media is found
            }
        }




        private string ExtractMediaIdentifier(string mediaUrl)
        {
            Match match = Regex.Match(mediaUrl, @"file_store/(\w+)");
            return match.Success ? match.Groups[1].Value : Guid.NewGuid().ToString();
        }

        private string ExtractThreadId(Uri threadUri)
        {
            Match match = Regex.Match(threadUri.AbsolutePath, @"res/(\d+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            else
            {
                Log.Error("Invalid thread URI: {ThreadUri}", threadUri);
                throw new ArgumentException("Invalid thread URI", nameof(threadUri));
            }
        }
    }
}

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
            // Call base class method for logging
            await base.RecheckThreadAsync(queue);

            try
            {
                Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);

                var client = Board.ImageBoard.GetHttpClient();
                HttpResponseMessage response = await client.GetAsync(ThreadUri);
                response.EnsureSuccessStatusCode();
                string htmlContent = await response.Content.ReadAsStringAsync();

                var doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                var imageNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'file')]//a[contains(@href, 'media')]");
                var uniqueImageUrls = new HashSet<string>();

                if (imageNodes != null)
                {
                    foreach (var node in imageNodes)
                    {
                        string imageUrl = node.GetAttributeValue("href", string.Empty);
                        string mediaIdentifier = ExtractMediaIdentifier(imageUrl);

                        // Skip already downloaded media
                        if (!IsMediaDownloaded(mediaIdentifier))
                        {
                            string fileName = node.InnerText.Trim();
                            string destinationPath = Path.Combine("Downloads", Board.BoardCode, ThreadId, fileName);

                            var downloadItem = new DownloadItem(new Uri(imageUrl), destinationPath, Board.ImageBoard, this, mediaIdentifier);
                            queue.EnqueueDownload(downloadItem);
                            Log.Debug("Enqueued download for image {ImageUrl}", imageUrl);
                        }
                        else
                        {
                            Log.Debug("Skipping already downloaded media {MediaIdentifier} for thread {ThreadId}", mediaIdentifier, ThreadId);
                        }
                    }

                    TotalMediaCount = uniqueImageUrls.Count;
                }
                else
                {
                    Log.Warning("No images found in thread {ThreadId}", ThreadId);
                }

                Log.Information("Recheck complete for thread {ThreadId}: Enqueued all new media downloads", ThreadId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error enqueuing media downloads for thread {ThreadId}: {Message}", ThreadId, ex.Message);
            }

            if (DownloadedMediaCount == TotalMediaCount)
            {
                Status = "Finished"; // Set status to "Finished" after recheck
            }
        }

        private string ExtractMediaIdentifier(string mediaUrl)
        {
            // Extract the unique identifier for media (e.g., the file hash or part of the URL)
            var match = System.Text.RegularExpressions.Regex.Match(mediaUrl, @"file_store/(\w+)");
            return match.Success ? match.Groups[1].Value : Guid.NewGuid().ToString();
        }

        private string ExtractThreadId(Uri threadUri)
        {
            var match = System.Text.RegularExpressions.Regex.Match(threadUri.AbsolutePath, @"res/(\d+)");
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

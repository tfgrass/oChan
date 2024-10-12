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
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
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

      private bool _isRechecking = false;

public override async Task RecheckThreadAsync(DownloadQueue queue)
{
    if (_isRechecking)
    {
        Log.Warning("Recheck for thread {ThreadId} is already in progress. Skipping new recheck.", ThreadId);
        return; // Prevent overlapping rechecks
    }

    _isRechecking = true; // Mark that a recheck is in progress
    Status = "Rechecking"; // Update the status to "Rechecking"
    
    try
    {
        await base.RecheckThreadAsync(queue);

        Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);

        HttpClient client = Board.ImageBoard.GetHttpClient();
        string apiUrl = ThreadUri.ToString().Replace(".html", ".json");
        HttpResponseMessage response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        string jsonContent = await response.Content.ReadAsStringAsync();

        JObject threadData = JObject.Parse(jsonContent);

        int previousTotalMediaCount = TotalMediaCount; // Preserve previous counts
        int newMediaCount = 0;
        HashSet<string> uniqueImageUrls = new HashSet<string>();

        foreach (JToken post in threadData["posts"])
        {
            // Check for media files and extra files
            newMediaCount += ProcessPostMedia(post, queue, uniqueImageUrls);
        }

        if (newMediaCount > 0)
        {
            TotalMediaCount = previousTotalMediaCount + newMediaCount;
            Status = "Downloading"; // Only set to "Downloading" if new media is enqueued
        }

        Log.Information("Recheck complete for thread {ThreadId}: Enqueued {NewMediaCount} new media downloads", ThreadId, newMediaCount);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error enqueuing media downloads for thread {ThreadId}: {Message}", ThreadId, ex.Message);
    }
    finally
    {
        _isRechecking = false; // Mark that the recheck is finished
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

private int ProcessPostMedia(JToken post, DownloadQueue queue, HashSet<string> uniqueImageUrls)
{
    int newMediaCount = 0;

    if (post["tim"] != null && post["ext"] != null)
    {
        string mediaIdentifier = post["tim"].ToString();
        string ext = post["ext"].ToString();
        string imageUrl = $"https://media.128ducks.com/file_store/{mediaIdentifier}{ext}";

        if (!IsMediaDownloaded(mediaIdentifier) && !uniqueImageUrls.Contains(imageUrl))
        {
            string fileName = $"{post["filename"]}{ext}";
            string destinationPath = Path.Combine("Downloads", Board.BoardCode, ThreadId, fileName);

            DownloadItem downloadItem = new DownloadItem(new Uri(imageUrl), destinationPath, Board.ImageBoard, this, mediaIdentifier);
            queue.EnqueueDownload(downloadItem);
            uniqueImageUrls.Add(imageUrl);
            newMediaCount++;
        }
    }

    // Handle extra files in the post
    if (post["extra_files"] != null)
    {
        foreach (JToken extraFile in post["extra_files"])
        {
            string mediaIdentifier = extraFile["tim"].ToString();
            string ext = extraFile["ext"].ToString();
            string imageUrl = $"https://media.128ducks.com/file_store/{mediaIdentifier}{ext}";

            if (!IsMediaDownloaded(mediaIdentifier) && !uniqueImageUrls.Contains(imageUrl))
            {
                string fileName = $"{extraFile["filename"]}{ext}";
                string destinationPath = Path.Combine("Downloads", Board.BoardCode, ThreadId, fileName);

                DownloadItem downloadItem = new DownloadItem(new Uri(imageUrl), destinationPath, Board.ImageBoard, this, mediaIdentifier);
                queue.EnqueueDownload(downloadItem);
                uniqueImageUrls.Add(imageUrl);
                newMediaCount++;
            }
        }
    }

    return newMediaCount;
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

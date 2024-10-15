namespace oChan.Boards.EightKun;

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using oChan.Boards.Base;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;

public class EightKunThread : BaseThread
{
    private readonly Config _config; // Reference to the config

    public override IBoard Board { get; }
    public override string ThreadId { get; }
    public override string Title { get; set; }
    public override string NiceName => $"{Title} ({ThreadId})";
    public override Uri ThreadUri { get; }

    public EightKunThread(IBoard board, Uri threadUri)
    {
        _config = Config.LoadConfig(); // Load the configuration
        Board = board ?? throw new ArgumentNullException(nameof(board));
        ThreadUri = threadUri ?? throw new ArgumentNullException(nameof(threadUri));

        ThreadId = ExtractThreadId(threadUri);
        Title = "Unknown";
        Status = "Pending";
        Log.Information("Initialized EightKunThread with ID: {ThreadId}", ThreadId);
    }

    private bool _isChecking = false;

    public override async Task checkThreadAsync(DownloadQueue queue)
    {
        if (_isChecking)
        {
            Log.Warning("Recheck for thread {ThreadId} is already in progress. Skipping new recheck.", ThreadId);
            return; // Prevent overlapping rechecks
        }

        _isChecking = true; // Mark that a recheck is in progress
        Status = "Checking"; // Update the status to "Rechecking"

        try
        {
            await base.checkThreadAsync(queue);

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
            _isChecking = false; // Mark that the recheck is finished
        }

        if (DownloadedMediaCount == TotalMediaCount && TotalMediaCount > 0)
        {
            Status = "Finished"; 
        }
        else if (TotalMediaCount == 0)
        {
            Status = "No media found"; 
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

            if (!IsMediaDownloaded(mediaIdentifier) && !queue.IsInQueue(imageUrl) && !uniqueImageUrls.Contains(imageUrl))
            {
                // Use the board's unique filename
                string fileName = $"{mediaIdentifier}{ext}";
                fileName = SanitizeFileName(fileName);
                string destinationPath = Path.Combine(_config.DownloadPath, Board.BoardCode, ThreadId, fileName);

                DownloadItem downloadItem = new DownloadItem(new Uri(imageUrl), destinationPath, Board.ImageBoard, this, mediaIdentifier);
                queue.EnqueueDownload(downloadItem);
                uniqueImageUrls.Add(imageUrl);
                newMediaCount++;
            }
        }

        if (post["extra_files"] != null)
        {
            foreach (JToken extraFile in post["extra_files"])
            {
                string mediaIdentifier = extraFile["tim"].ToString();
                string ext = extraFile["ext"].ToString();
                string imageUrl = $"https://media.128ducks.com/file_store/{mediaIdentifier}{ext}";

                if (!IsMediaDownloaded(mediaIdentifier) && !queue.IsInQueue(imageUrl) && !uniqueImageUrls.Contains(imageUrl))
                {
                    // Use the board's unique filename
                    string fileName = $"{mediaIdentifier}{ext}";
                    fileName = SanitizeFileName(fileName);
                    string destinationPath = Path.Combine(_config.DownloadPath, Board.BoardCode, ThreadId, fileName);

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

    private string SanitizeFileName(string fileName)
    {
        // Replace any invalid file name characters with underscores
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
        {
            fileName = fileName.Replace(c, '_');
        }
        return fileName;
    }
}

namespace oChan.Boards.FourChan;

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json.Linq;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Boards.Base;
using Serilog;

public class FourChanThread : BaseThread
{
    public override IBoard Board { get; }
    public override string ThreadId { get; }
    public override string Title { get; set; }
    public override string NiceName => $"{Title} ({ThreadId})";
    public override Uri ThreadUri { get; }

    public FourChanThread(IBoard board, Uri threadUri)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        ThreadUri = threadUri ?? throw new ArgumentNullException(nameof(threadUri));

        ThreadId = ExtractThreadId(threadUri);
        Title = "Unknown";
        Status = "Pending";
        Log.Information("Initialized FourChanThread with ID: {ThreadId}", ThreadId);
    }

    [RequiresUnreferencedCode("dynamic json shit")]
    public override async Task checkThreadAsync(DownloadQueue queue)
    {
        // Update status to "Rechecking" at the start of the recheck
        Status = "Checking";
        await base.checkThreadAsync(queue);

        Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);

        try
        {
            HttpClient client = Board.ImageBoard.GetHttpClient();
            string boardCode = ((FourChanBoard)Board).BoardCode;
            string threadJsonUrl = $"https://a.4cdn.org/{boardCode}/thread/{ThreadId}.json";
            HttpResponseMessage response = await client.GetAsync(threadJsonUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            JObject threadData = JObject.Parse(json);

            // Update thread title if available
            Title = threadData["posts"]?[0]?["sub"]?.ToString() ?? $"Thread {ThreadId}";

            JArray posts = threadData["posts"] as JArray;
            if (posts == null)
            {
                Log.Error("No 'posts' found in thread data for thread {ThreadId}", ThreadId);
                throw new Exception("Thread data does not contain 'posts'.");
            }

            IEnumerable<dynamic> postsWithImages = posts
                .Where(x => x["ext"] != null && x["tim"] != null && x["filename"] != null)
                .Select(x => new
                {
                    Tim = x.Value<long?>("tim") ?? 0,
                    Ext = x.Value<string>("ext") ?? string.Empty,
                    Filename = x.Value<string>("filename") ?? "unknown"
                });

            TotalMediaCount = postsWithImages.Count();

            if (TotalMediaCount > 0)
            {
                Status = "Downloading"; // Set status to "Downloading" if new images are being downloaded
            }

            foreach (dynamic post in postsWithImages)
            {
                if (string.IsNullOrWhiteSpace(post.Ext) || post.Tim == 0) continue; // Skip invalid images

                string mediaIdentifier = post.Tim.ToString();

                // Skip already downloaded media
                if (IsMediaDownloaded(mediaIdentifier))
                {
                    Log.Debug("Skipping already downloaded media {MediaIdentifier} for thread {ThreadId}", mediaIdentifier, ThreadId);
                    continue;
                }

                string imageUrl = $"https://i.4cdn.org/{boardCode}/{post.Tim}{post.Ext}";
                string destinationPath = Path.Combine("Downloads", boardCode, ThreadId, $"{post.Filename}{post.Ext}");

                DownloadItem downloadItem = new DownloadItem(new Uri(imageUrl), destinationPath, Board.ImageBoard, this, mediaIdentifier);
                queue.EnqueueDownload(downloadItem);
                Log.Debug("Enqueued download for image {ImageUrl}", imageUrl);
            }

            Log.Information("Recheck complete for thread {ThreadId}: Enqueued all new media downloads", ThreadId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error enqueuing media downloads for thread {ThreadId}: {Message}", ThreadId, ex.Message);
        }

        // Set status to "Finished" if all media has been downloaded
        if (DownloadedMediaCount == TotalMediaCount)
        {
            Status = "Finished";
        }
    }

    private string ExtractThreadId(Uri threadUri)
    {
        Match match = Regex.Match(threadUri.AbsolutePath, @"thread/(\d+)");
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

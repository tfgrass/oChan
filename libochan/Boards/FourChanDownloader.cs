using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using oChan;

namespace oChan.Boards
{
    public class FourChanDownloader : Downloader
    {
        private readonly Config _config;
        private static readonly HttpClient _httpClient = new HttpClient();

        public FourChanDownloader()
        {
            _config = new Config();
        }

        public override bool CanHandle(string url)
        {
            return url.Contains("https://boards.4chan.org/");
        }

        public override async Task DownloadThreadAsync(string url)
        {
            updateProps(url, "0/0", "Idle");
            string status = "Downloading";

            try
            {
                // Parse board and thread ID from URL
                string boardCode = ExtractBoardCode(url);
                string threadId = ExtractThreadId(url);

                // Prepare folder for saving images
                string threadFolder = Path.Combine(_config.DownloadPath, threadId);
                Directory.CreateDirectory(threadFolder);

                // Fetch thread JSON with retry mechanism
                string threadJsonUrl = $"http://a.4cdn.org/{boardCode}/thread/{threadId}.json";
                Console.WriteLine("Thread JSON URL: " + threadJsonUrl);
                string jsonResponse = await FetchWithRetryAsync(threadJsonUrl);
                var jObject = JObject.Parse(jsonResponse);
Console.WriteLine("Thread JSON: " + jsonResponse);
                // Extract image URLs
                var postsWithImages = jObject
                    .SelectTokens("posts[*]")
                    .Where(x => x["ext"] != null && x["tim"] != null && x["filename"] != null)
                    .Select(x => new
                    {
                        Tim = x["tim"]?.Value<long>() ?? 0,
                        Ext = x["ext"]?.Value<string>() ?? string.Empty,
                        Filename = x["filename"]?.Value<string>() ?? "unknown"
                    });

                int totalImages = postsWithImages.Count();
                int downloadedImages = 0;

                // Download each image
                foreach (var post in postsWithImages)
                {
                    if (string.IsNullOrWhiteSpace(post.Ext) || post.Tim == 0) continue; // Skip invalid images

                    string imageUrl = $"http://i.4cdn.org/{boardCode}/{post.Tim}{post.Ext}";
                    string filePath = Path.Combine(threadFolder, $"{post.Filename}{post.Ext}");

                    try
                    {
                        await Utils.DownloadFileAsync(imageUrl, filePath);
                        Task.Delay(1000).Wait(); // Delay to avoid rate limiting
                        downloadedImages++;
                    }
                    catch (Exception downloadEx)
                    {
                        Console.WriteLine($"Error downloading image {post.Filename}: {downloadEx.Message}");
                    }

                    updateProps(url, $"{downloadedImages}/{totalImages}", status);
                }

                Status = "Completed";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading thread: {ex.Message}");
                Status = "Failed";
            }
        }

        private async Task<string> FetchWithRetryAsync(string url)
        {
            int retryCount = 0;
            const int maxRetries = 5;
            const int delayBetweenRetries = 5000; // 5 seconds
   // Define a User-Agent to mimic a browser
    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/85.0.4183.121 Safari/537.36");

            while (retryCount < maxRetries)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync();
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        Console.WriteLine("Rate-limited, retrying...");
                        await Task.Delay(delayBetweenRetries); // Wait before retrying
                        retryCount++;
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode(); // Throw for non-success status codes
                    }
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"HTTP error: {ex.Message}");
                    throw; // If other HTTP error, rethrow the exception
                }
            }

            throw new Exception("Failed to fetch thread data after multiple retries.");
        }

        private string ExtractBoardCode(string url)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, "(?<=boards.4chan.org/)[a-zA-Z0-9]+(?=/thread)");
            return match.Success ? match.Value : throw new ArgumentException("Invalid 4chan URL");
        }

        private string ExtractThreadId(string url)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, "(?<=thread/)[0-9]+");
            return match.Success ? match.Value : throw new ArgumentException("Invalid 4chan URL");
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

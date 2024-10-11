namespace oChan;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;

public static class Utils
{
    private static readonly HttpClient _httpClient = new HttpClient();

    static Utils()
    {
        // Define a User-Agent to mimic a browser
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/85.0.4183.121 Safari/537.36");
    }

    public static async Task DownloadFileAsync(string url, string filePath)
    {
        int retryCount = 0;
        const int maxRetries = 5;
        const int delayBetweenRetries = 5000; // 5 seconds

        while (retryCount < maxRetries)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var fileBytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(filePath, fileBytes);
                    Log.Information("Downloaded file to {FilePath}", filePath);
                    return; // Exit method after successful download
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Log.Warning("Rate-limited while downloading {Url}. Retrying...", url);
                    await Task.Delay(delayBetweenRetries); // Wait before retrying
                    retryCount++;
                }
                else
                {
                    response.EnsureSuccessStatusCode(); // Throw for other non-success status codes
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, "Error downloading file from {Url}: {Message}", url, ex.Message);
                if (retryCount >= maxRetries)
                {
                    throw; // If max retries reached, rethrow the exception
                }
                await Task.Delay(delayBetweenRetries); // Wait before retrying
                retryCount++;
            }
        }

        Log.Error("Failed to download file from {Url} after {MaxRetries} attempts.", url, maxRetries);
        throw new Exception($"Failed to download file from {url} after {maxRetries} attempts.");
    }
}

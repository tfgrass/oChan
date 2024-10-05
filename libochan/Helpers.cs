using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

public static class Utils
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task DownloadFileAsync(string url, string filePath)
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
                using var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var fileBytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(filePath, fileBytes);
                    Console.WriteLine($"Downloaded: {filePath}");
                    return; // Exit method after successful download
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Console.WriteLine("Rate-limited while downloading, retrying...");
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
                Console.WriteLine($"Error downloading file: {ex.Message}");
                if (retryCount >= maxRetries)
                {
                    throw; // If max retries reached, rethrow the exception
                }
                await Task.Delay(delayBetweenRetries); // Wait before retrying
                retryCount++;
            }
        }

        throw new Exception($"Failed to download file after {maxRetries} attempts.");
    }
}

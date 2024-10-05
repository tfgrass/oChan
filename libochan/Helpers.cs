using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

public static class Utils
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task DownloadFileAsync(string url, string filePath)
    {
        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var fileBytes = await response.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(filePath, fileBytes);
    }
}

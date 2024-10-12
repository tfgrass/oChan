namespace oChan.Boards.FourChan;

using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using oChan.Boards.Base;
using oChan.Interfaces;
using Serilog;

public class FourChanBoard : BaseBoard
{
    public override IImageBoard ImageBoard { get; }
    public override string BoardCode { get; }
    public override string Name { get; }
    public override string NiceName => $"/{BoardCode}/ - {Name}";
    public override Uri BoardUri { get; }

    public FourChanBoard(IImageBoard imageBoard, Uri boardUri)
    {
        ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
        BoardUri = boardUri ?? throw new ArgumentNullException(nameof(boardUri));

        // Extract board code from URI
        BoardCode = ExtractBoardCode(boardUri);
        Name = BoardCode; // 4chan doesn't provide board names in the URL
        Log.Information("Initialized FourChanBoard for /{BoardCode}/", BoardCode);
    }

    public override async Task<IEnumerable<IThread>> GetThreadsAsync()
    {
        Log.Debug("Fetching threads for board /{BoardCode}/", BoardCode);
        List<IThread> threads = new List<IThread>();

        try
        {
            HttpClient client = ImageBoard.GetHttpClient();
            string catalogUrl = $"https://a.4cdn.org/{BoardCode}/catalog.json";
            HttpResponseMessage response = await client.GetAsync(catalogUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            List<dynamic> catalogData = JsonConvert.DeserializeObject<List<dynamic>>(json);

            if (catalogData == null)
            {
                Log.Error("Failed to deserialize catalog data for board /{BoardCode}/", BoardCode);
                throw new Exception("Catalog data is null.");
            }

            foreach (dynamic page in catalogData)
            {
                if (page.threads == null) continue;

                foreach (dynamic thread in page.threads)
                {
                    string threadId = thread.no.ToString();
                    Uri threadUri = new Uri($"https://boards.4chan.org/{BoardCode}/thread/{threadId}");
                    threads.Add(new FourChanThread(this, threadUri));
                }
            }

            Log.Information("Fetched {ThreadCount} threads for board /{BoardCode}/", threads.Count, BoardCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching threads for board /{BoardCode}/: {Message}", BoardCode, ex.Message);
            throw;
        }

        return threads;
    }

    private string ExtractBoardCode(Uri boardUri)
    {
        string[] segments = boardUri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length > 0)
        {
            return segments[0];
        }
        else
        {
            Log.Error("Invalid board URI: {BoardUri}", boardUri);
            throw new ArgumentException("Invalid board URI", nameof(boardUri));
        }
    }
}

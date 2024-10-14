namespace oChan.Boards.EightKun;

using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using oChan.Boards.Base;
using oChan.Interfaces;
using Serilog;

public class EightKunBoard : BaseBoard
{
    public override IImageBoard ImageBoard { get; }
    public override string BoardCode { get; }
    public override string Name { get; protected set; }  // Allow setting the Name property
    public override string NiceName => $"/{BoardCode}/ - {Name}";
    public override Uri BoardUri { get; }

    public EightKunBoard(IImageBoard imageBoard, Uri boardUri)
    {
        ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
        BoardUri = boardUri ?? throw new ArgumentNullException(nameof(boardUri));

        BoardCode = ExtractBoardCode(boardUri);
        Name = BoardCode;  // Set Name initially to BoardCode

        // Fetch board name asynchronously
        Task.Run(async () => await FetchBoardNameAsync());

        Log.Information("Initialized EightKunBoard for /{BoardCode}/", BoardCode);
    }

    private async Task FetchBoardNameAsync()
    {
        try
        {
            HttpClient client = ImageBoard.GetHttpClient();
            string boardsUrl = "https://8kun.top/boards.json";  // Use API to get metadata for boards
            HttpResponseMessage response = await client.GetAsync(boardsUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            JArray boardsData = JArray.Parse(json);

            foreach (JToken board in boardsData)
            {
                if (board["uri"].ToString() == BoardCode)
                {
                    Name = board["title"].ToString();  // Assign the board name to the Name property
                    Log.Information("Fetched board name: {BoardName} for /{BoardCode}/", Name, BoardCode);
                    break;
                }
            }

            if (Name == BoardCode)
            {
                Log.Warning("Board name not found for /{BoardCode}/, using BoardCode as name", BoardCode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching board name for /{BoardCode}/", BoardCode);
        }
    }

    public override async Task<IEnumerable<IThread>> GetThreadsAsync()
    {
        Log.Debug("Fetching threads for board /{BoardCode}/", BoardCode);
        List<IThread> threads = new List<IThread>();

        try
        {
            HttpClient client = ImageBoard.GetHttpClient();
            string catalogUrl = $"https://8kun.top/{BoardCode}/catalog.json";
            HttpResponseMessage response = await client.GetAsync(catalogUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            JArray catalogData = JArray.Parse(json);

            foreach (JToken page in catalogData)
            {
                foreach (JToken thread in page["threads"])
                {
                    string threadId = thread["no"].ToString();
                    Uri threadUri = new Uri($"https://8kun.top/{BoardCode}/res/{threadId}.html");
                    threads.Add(new EightKunThread(this, threadUri));
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

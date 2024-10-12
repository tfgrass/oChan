using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using oChan.Boards.Base;
using oChan.Interfaces;
using Serilog;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace oChan.Boards.EightKun
{
    public class EightKunBoard : BaseBoard
    {
        public override IImageBoard ImageBoard { get; }
        public override string BoardCode { get; }
        public override string Name { get; }
        public override string NiceName => $"/{BoardCode}/ - {Name}";
        public override Uri BoardUri { get; }

        public EightKunBoard(IImageBoard imageBoard, Uri boardUri)
        {
            ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
            BoardUri = boardUri ?? throw new ArgumentNullException(nameof(boardUri));
            BoardCode = ExtractBoardCode(boardUri);
            Name = BoardCode;
            Log.Information("Initialized EightKunBoard for /{BoardCode}/", BoardCode);
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
                var catalogData = JArray.Parse(json);

                foreach (var page in catalogData)
                {
                    foreach (var thread in page["threads"])
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
            var match = System.Text.RegularExpressions.Regex.Match(boardUri.AbsolutePath, @"^/(\w+)/?");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            else
            {
                Log.Error("Invalid board URI: {BoardUri}", boardUri);
                throw new ArgumentException("Invalid board URI", nameof(boardUri));
            }
        }
    }
}

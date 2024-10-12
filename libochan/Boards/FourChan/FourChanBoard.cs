using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using oChan.Boards.Base;
using oChan.Interfaces;
using Serilog;
using Newtonsoft.Json;

namespace oChan.Boards.FourChan
{
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
            var threads = new List<IThread>();

            try
            {
                var client = ImageBoard.GetHttpClient();
                string catalogUrl = $"https://a.4cdn.org/{BoardCode}/catalog.json";
                var response = await client.GetAsync(catalogUrl);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var catalogData = JsonConvert.DeserializeObject<List<dynamic>>(json);

                if (catalogData == null)
                {
                    Log.Error("Failed to deserialize catalog data for board /{BoardCode}/", BoardCode);
                    throw new Exception("Catalog data is null.");
                }

                foreach (var page in catalogData)
                {
                    if (page.threads == null) continue;

                    foreach (var thread in page.threads)
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

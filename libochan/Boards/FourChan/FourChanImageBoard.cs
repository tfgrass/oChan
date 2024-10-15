// File Path: ./Boards/FourChan/FourChanImageBoard.cs

using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using oChan.Boards.Base;
using oChan.Interfaces;
using Serilog;

namespace oChan.Boards.FourChan
{
    public class FourChanImageBoard : BaseImageBoard
    {
        public override string Name => "4chan";
        public override string NiceName => "4chan";
        public override Uri BaseUri => new Uri("https://boards.4chan.org");

        public override bool CanHandle(Uri uri)
        {
            bool canHandle = uri.Host.Contains(BaseUri.Host);
            Log.Debug("FourChanImageBoard CanHandle({Uri}) returned {Result}", uri, canHandle);
            return canHandle;
        }

        public override bool IsThreadUri(Uri uri)
        {
            return Regex.IsMatch(uri.AbsolutePath, @"^/\w+/thread/\d+");
        }

        // Updated regex to accept /w/ and /w/catalog
        public override bool IsBoardUri(Uri uri)
        {
            // This regex matches:
            // - /w
            // - /w/
            // - /w/catalog
            // - /w/catalog/
            return Regex.IsMatch(uri.AbsolutePath, @"^/\w+(/catalog)?/?$");
        }

        public override IBoard GetBoard(Uri boardUri)
        {
            if (boardUri == null)
            {
                Log.Error("Board URI is null.");
                throw new ArgumentNullException(nameof(boardUri));
            }

            // Normalize the board URI by removing /catalog if present
            string path = boardUri.AbsolutePath;
            if (path.EndsWith("/catalog", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - "/catalog".Length);
                // Ensure the path ends with a slash
                if (!path.EndsWith("/"))
                {
                    path += "/";
                }
                boardUri = new Uri($"{BaseUri}{path}");
                Log.Debug("Normalized board URI by removing /catalog: {BoardUri}", boardUri);
            }

            Log.Information("Creating FourChanBoard for URI: {BoardUri}", boardUri);
            return new FourChanBoard(this, boardUri);
        }

        public override IThread GetThread(Uri threadUri)
        {
            if (threadUri == null)
            {
                Log.Error("Thread URI is null.");
                throw new ArgumentNullException(nameof(threadUri));
            }

            Log.Information("Creating FourChanThread for URI: {ThreadUri}", threadUri);

            // Extract board code from the thread URI to create the board instance
            string boardPath = threadUri.AbsolutePath.Split("/thread")[0];
            Uri boardUri = new Uri($"https://boards.4chan.org{boardPath}");
            FourChanBoard board = (FourChanBoard)GetBoard(boardUri);

            return new FourChanThread(board, threadUri);
        }

        public override HttpClient GetHttpClient()
        {
            HttpClient client = base.GetHttpClient();
            // Set a User-Agent to mimic a browser
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/85.0.4183.121 Safari/537.36");
            Log.Debug("Configured HttpClient for FourChanImageBoard.");
            return client;
        }
    }
}

namespace oChan
{
    using System;
    using System.Collections.Generic;
    using Serilog;
    using oChan.Interfaces;
    using oChan.Boards.FourChan;
    using oChan.Boards.EightKun;

    public class Registry
    {
        // Store singleton instances of image boards in a dictionary
        private readonly Dictionary<Type, IImageBoard> _registeredImageBoards = new();
        private readonly HashSet<string> _processedThreadUrls = new();  // Keep track of added thread URLs
        private readonly HashSet<string> _processedBoardUrls = new();   // Keep track of added board URLs

        public Registry()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Set the minimum log level
                .WriteTo.Console()      // Output logs to the console
                .CreateLogger();

            Log.Information("Registry initialized with logging configured.");

            // Register image boards here
            RegisterImageBoards();
        }

        private void RegisterImageBoards()
        {
            // Create and register the FourChanImageBoard
            RegisterImageBoard(new FourChanImageBoard());

            // Register 8kun
            RegisterImageBoard(new EightKunImageBoard());

            // ... other image boards
        }

        // Register a new image board in the registry (by instance)
        private void RegisterImageBoard(IImageBoard imageBoard)
        {
            if (imageBoard == null)
            {
                Log.Error("Attempted to register a null image board.");
                throw new ArgumentNullException(nameof(imageBoard));
            }

            Type imageBoardType = imageBoard.GetType();

            // Ensure the image board is only registered once
            if (!_registeredImageBoards.ContainsKey(imageBoardType))
            {
                _registeredImageBoards[imageBoardType] = imageBoard;
                Log.Information("Registered image board: {ImageBoardName}", imageBoardType.Name);
            }
            else
            {
                Log.Warning("Image board {ImageBoardName} is already registered.", imageBoardType.Name);
            }
        }

        // Get a thread instance for a given thread URL and prevent duplicate entries
        public IThread? GetThread(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Log.Error("URL is null or empty.");
                throw new ArgumentNullException(nameof(url));
            }

            if (_processedThreadUrls.Contains(url))
            {
                Log.Warning("Thread URL {Url} is already added.", url);
                return null; // Prevent adding the same thread URL multiple times
            }

            Uri uri;
            try
            {
                uri = new Uri(url);
            }
            catch (UriFormatException ex)
            {
                Log.Error(ex, "Invalid URL format: {Url}", url);
                return null;
            }

            foreach (IImageBoard imageBoard in _registeredImageBoards.Values)
            {
                if (imageBoard.CanHandle(uri) && imageBoard.IsThreadUri(uri))
                {
                    Log.Debug("Image board {ImageBoardName} identified URL as thread: {Url}", imageBoard.GetType().Name, url);

                    IThread thread = imageBoard.GetThread(uri);
                    _processedThreadUrls.Add(url);  // Mark the URL as processed

                    return thread;
                }
            }

            Log.Warning("No image board found for thread URL: {Url}", url);
            return null;
        }

        // Get a board instance for a given board URL and prevent duplicate entries
        public IBoard? GetBoard(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Log.Error("URL is null or empty.");
                throw new ArgumentNullException(nameof(url));
            }

            if (_processedBoardUrls.Contains(url))
            {
                Log.Warning("Board URL {Url} is already added.", url);
                return null; // Prevent adding the same board URL multiple times
            }

            Uri uri;
            try
            {
                uri = new Uri(url);
            }
            catch (UriFormatException ex)
            {
                Log.Error(ex, "Invalid URL format: {Url}", url);
                return null;
            }

            foreach (IImageBoard imageBoard in _registeredImageBoards.Values)
            {
                if (imageBoard.CanHandle(uri) && imageBoard.IsBoardUri(uri))
                {
                    Log.Debug("Image board {ImageBoardName} identified URL as board: {Url}", imageBoard.GetType().Name, url);

                    IBoard board = imageBoard.GetBoard(uri);
                    _processedBoardUrls.Add(url);  // Mark the URL as processed

                    return board;
                }
            }

            Log.Warning("No image board found for board URL: {Url}", url);
            return null;
        }
    }
}

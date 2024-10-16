namespace oChan
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;  // For detecting the OS
    using Serilog;
    using oChan.Interfaces;
    using oChan.Boards.FourChan;
    using oChan.Boards.EightKun;

    public class Registry
    {
        private readonly Config _config;  // Config object to store app settings
        private readonly Dictionary<Type, IImageBoard> _registeredImageBoards = new();
        private readonly HashSet<string> _processedThreadUrls = new();  // Keep track of added thread URLs
        private readonly HashSet<string> _processedBoardUrls = new();   // Keep track of added board URLs
        private readonly Dictionary<string, IThread> _activeThreads = new(); // Track active threads by URL

        public Registry()
        {
            // Set up logging configuration
            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Debug()   // Set the minimum log level
                .WriteTo.Console();     // Output logs to the console


            loggerConfig.WriteTo.File("logs/oChan.log", rollingInterval: RollingInterval.Day);  // Log to file on Windows

            Log.Logger = loggerConfig.CreateLogger();

            Log.Information("Registry initialized with logging configured.");

            // Initialize the Config object by loading from file or creating default
            _config = Config.LoadConfig();
            Log.Information("Configuration loaded.");

            // Print out loaded config (for debug purposes)
            _config.PrintConfig();

            // Register image boards here
            RegisterImageBoards();
        }

        // Method to expose the Config object so it can be used elsewhere
        public Config GetConfig() => _config;

        private void RegisterImageBoards()
        {
            // Create and register the FourChanImageBoard
            RegisterImageBoard(new FourChanImageBoard());

            // Register 8kun
            RegisterImageBoard(new EightKunImageBoard());

            // Register other image boards here if needed
        }

        // Register a new image board in the registry
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
                    _activeThreads[url] = thread;   // Store the active thread

                    return thread;
                }
            }

            Log.Warning("No image board found for thread URL: {Url}", url);
            return null;
        }

        // Remove a thread URL from the processed list and active threads
        public void RemoveThread(string url)
        {
            if (_processedThreadUrls.Contains(url))
            {
                _processedThreadUrls.Remove(url);
            }
            else
            {
                Log.Warning("Attempted to remove thread URL {Url} that was not processed.", url);
            }

            // Ensure the thread is also removed from active threads
            if (_activeThreads.ContainsKey(url))
            {
                _activeThreads.Remove(url);
                Log.Information("Removed thread {Url} from active threads.", url);
            }
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

        // Remove a board URL from the processed list
        public void RemoveBoard(string url)
        {
            if (_processedBoardUrls.Contains(url))
            {
                _processedBoardUrls.Remove(url);
                Log.Information("Removed board URL {Url} from processed list.", url);
            }
            else
            {
                Log.Warning("Attempted to remove board URL {Url} that was not processed.", url);
            }
        }
    }
}

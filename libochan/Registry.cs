namespace oChan;
using System;
using System.Collections.Generic;
using Serilog;
using oChan.Interfaces;
using oChan.Boards.FourChan;
using oChan.Boards.EightKun; // Include the namespace for FourChanImageBoard

public class Registry
{
    // Store singleton instances of image boards in a dictionary
    private readonly Dictionary<Type, IImageBoard> _registeredImageBoards = new();

    public Registry()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug() // Set the minimum log level
            .WriteTo.Console()    // Output logs to the console
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

        // ... other downloaders
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

    // Find an image board that can handle the URL and return the IThread
    public IThread? HandleUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Log.Error("URL is null or empty.");
            throw new ArgumentNullException(nameof(url));
        }

        Uri uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException ex)
        {
            Log.Error(ex, "Invalid URL format: {Url}", url);
            throw;
        }

        foreach (IImageBoard imageBoard in _registeredImageBoards.Values)
        {
            if (imageBoard.CanHandle(uri))
            {
                Log.Debug("Image board {ImageBoardName} can handle URL: {Url}", imageBoard.GetType().Name, url);

                // Get the thread from the image board
                IThread thread = imageBoard.GetThread(uri);

                return thread;
            }
        }

        Log.Warning("No image board found for URL: {Url}", url);
        return null;
    }

    // List all registered image boards
    public void ListImageBoards()
    {
        if (_registeredImageBoards.Count == 0)
        {
            Log.Information("No image boards registered.");
        }
        else
        {
            foreach (Type imageBoardType in _registeredImageBoards.Keys)
            {
                Log.Information("Registered image board: {ImageBoardName}", imageBoardType.Name);
            }
        }
    }
}

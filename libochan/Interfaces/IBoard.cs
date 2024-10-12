using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using oChan.Downloader;

namespace oChan.Interfaces
{
    /// <summary>
    /// Represents a specific board within an imageboard platform (e.g., "/g/" on 4chan).
    /// </summary>
    public interface IBoard
    {
        /// <summary>
        /// Gets the parent imageboard.
        /// </summary>
        IImageBoard ImageBoard { get; }

        /// <summary>
        /// Gets the board code (e.g., "g" for /g/).
        /// </summary>
        string BoardCode { get; }

        /// <summary>
        /// Gets the name of the board (e.g., "Technology").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets a user-friendly name for the board.
        /// </summary>
        string NiceName { get; }

        /// <summary>
        /// Gets the URI of the board.
        /// </summary>
        Uri BoardUri { get; }

        /// <summary>
        /// Fetches the threads on the board.
        /// </summary>
        /// <returns>A task representing the asynchronous operation, containing a collection of <see cref="IThread"/> instances.</returns>
        Task<IEnumerable<IThread>> GetThreadsAsync();

        /// <summary>
        /// Archives the board content based on the provided options.
        /// </summary>
        /// <param name="options">The archiving options.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ArchiveAsync(ArchiveOptions options);

        /// <summary>
        /// Starts monitoring the board for new threads.
        /// </summary>
        /// <param name="intervalInSeconds">The interval in seconds between checks.</param>
        void StartMonitoring(int intervalInSeconds);

        /// <summary>
        /// Stops monitoring the board.
        /// </summary>
        void StopMonitoring();

        /// <summary>
        /// Occurs when a new thread is discovered on the board.
        /// </summary>
        event EventHandler<ThreadEventArgs> ThreadDiscovered;
    }
}


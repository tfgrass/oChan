using System;
using System.Net.Http;

namespace oChan.Interfaces
{
    /// <summary>
    /// Represents an imageboard platform (e.g., 4chan).
    /// </summary>
    public interface IImageBoard
    {
        /// <summary>
        /// Gets the name of the imageboard (e.g., "4chan").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets a user-friendly name for the imageboard.
        /// </summary>
        string NiceName { get; }

        /// <summary>
        /// Gets the base URL of the imageboard.
        /// </summary>
        Uri BaseUri { get; }

        /// <summary>
        /// Determines if the imageboard can handle the given URI.
        /// </summary>
        /// <param name="uri">The URI to check.</param>
        /// <returns>True if the imageboard can handle the URI; otherwise, false.</returns>
        bool CanHandle(Uri uri);

        /// <summary>
        /// Creates an <see cref="IBoard"/> instance for the given board URI.
        /// </summary>
        /// <param name="boardUri">The URI of the board.</param>
        /// <returns>An instance of <see cref="IBoard"/>.</returns>
        IBoard GetBoard(Uri boardUri);

        /// <summary>
        /// Creates an <see cref="IThread"/> instance for the given thread URI.
        /// </summary>
        /// <param name="threadUri">The URI of the thread.</param>
        /// <returns>An instance of <see cref="IThread"/>.</returns>
        IThread GetThread(Uri threadUri);

        /// <summary>
        /// Provides an <see cref="HttpClient"/> configured with rate limiting for the imageboard.
        /// </summary>
        /// <returns>An instance of <see cref="HttpClient"/>.</returns>
        HttpClient GetHttpClient();
    }
}

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using oChan.Downloader;

namespace oChan.Interfaces
{
    /// <summary>
    /// Represents a specific thread within a board.
    /// </summary>
    public interface IThread
    {
        /// <summary>
        /// Gets the parent board.
        /// </summary>
        IBoard Board { get; }

        /// <summary>
        /// Gets the unique identifier of the thread.
        /// </summary>
        string ThreadId { get; }

        /// <summary>
        /// Gets the title or subject of the thread.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Gets a user-friendly name for the thread.
        /// </summary>
        string NiceName { get; }

        /// <summary>
        /// Gets the URI of the thread.
        /// </summary>
        Uri ThreadUri { get; }

        /// <summary>
        /// Archives the thread content based on the provided options.
        /// </summary>
        /// <param name="options">The archiving options.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ArchiveAsync(ArchiveOptions options);

        /// <summary>
        /// Enqueues media files in the thread for downloading.
        /// </summary>
        /// <param name="queue">The downloader queue to enqueue downloads to.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task EnqueueMediaDownloadsAsync(DownloadQueue queue);

        /// <summary>
        /// Gets or sets the collection of media identifiers that have been downloaded.
        /// </summary>
        HashSet<string> DownloadedMedia { get; set; }

        /// <summary>
        /// Checks if a media item has been downloaded.
        /// </summary>
        /// <param name="mediaIdentifier">The unique identifier of the media item.</param>
        /// <returns>True if the media item has been downloaded; otherwise, false.</returns>
        bool IsMediaDownloaded(string mediaIdentifier);

        /// <summary>
        /// Marks a media item as downloaded.
        /// </summary>
        /// <param name="mediaIdentifier">The unique identifier of the media item.</param>
        void MarkMediaAsDownloaded(string mediaIdentifier);
    }
}

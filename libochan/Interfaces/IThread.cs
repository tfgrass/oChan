using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using oChan.Downloader;

namespace oChan.Interfaces
{
    /// <summary>
    /// Represents a specific thread within a board.
    /// </summary>
    public interface IThread : INotifyPropertyChanged
    {
        /// <summary>
        /// Gets the parent board.
        /// </summary>
        IBoard Board { get; }
        event Action<IThread> ThreadRemoved;
        /// <summary>
        /// Gets the unique identifier of the thread.
        /// </summary>
        string ThreadId { get; }

        /// <summary>
        /// Gets or sets the title or subject of the thread.
        /// </summary>
        string Title { get; set; }

        /// <summary>
        /// Gets a user-friendly name for the thread.
        /// </summary>
        string NiceName { get; }

        /// <summary>
        /// Gets the URI of the thread.
        /// </summary>
        Uri ThreadUri { get; }

        /// <summary>
        /// Gets or sets the status of the thread.
        /// </summary>
        string Status { get; set; }

        /// <summary>
        /// Gets the progress of media downloads.
        /// </summary>
        string Progress { get; }

        /// <summary>
        /// Gets the URL of the thread.
        /// </summary>
        string Url { get; }

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

        /// <summary>
        /// Rechecks the thread for new posts and updates the download queue with new media.
        /// </summary>
        /// <param name="queue">The downloader queue to enqueue downloads to.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task checkThreadAsync(DownloadQueue queue);

        /// <summary>
        /// Loads the set of previously downloaded media from the filesystem (e.g., from .downloaded.json).
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task LoadDownloadedMediaAsync();

        /// <summary>
        /// Saves the set of downloaded media to the filesystem (e.g., to .downloaded.json).
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SaveDownloadedMediaAsync();

        /// <summary>
        /// Notifies that the thread should be removed (e.g., when a 404 occurs).
        /// </summary>
        void NotifyThreadRemoval();
    }
}

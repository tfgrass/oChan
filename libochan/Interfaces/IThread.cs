using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using oChan.Downloader;
using oChan.Http;

namespace oChan.Interfaces
{
    /// <summary>The availability of the source, separate from the state of media downloads.</summary>
    public enum ThreadSourceState
    {
        Active,
        Archived,
        NotFound
    }

    public enum ThreadActivity { Pending, Checking, Downloading, Finished, NoMedia, Error, NotFound, Stopped }

    /// <summary>
    /// Represents a specific thread within a board.
    /// </summary>
    public interface IThread : INotifyPropertyChanged
    {
        /// <summary>
        /// Gets the parent board.
        /// </summary>
        IBoard Board { get; }
        /// <summary>Registers the exclusive session owner. This reliable control callback must return promptly.</summary>
        IDisposable RegisterRemovalSink(Action<IThread> sink);
        ThreadSnapshot GetSnapshot(DownloadCounts downloads);
        ThreadActivity Activity { get; set; }
        FailureInfo? LastCheckFailure { get; }
        /// <summary>
        /// Gets the unique identifier of the thread.
        /// </summary>
        string ThreadId { get; }
        string DownloadDirectory { get; }
        ThreadSourceState SourceState { get; }

        /// <summary>The current HTTP admission, cooldown, or sending phase; null when no request is active.</summary>
        RequestPolicyProgress? RequestProgress { get; }

        /// <summary>Gets a snapshot of media errors retained until those media are saved successfully.</summary>
        IReadOnlyDictionary<string, string> MediaFailures { get; }

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
        /// Gets a display projection of the typed Activity.
        /// </summary>
        string Status { get; }

        /// <summary>
        /// Gets the progress of media downloads.
        /// </summary>
        string Progress { get; }

        /// <summary>Gets the number of known media items.</summary>
        int TotalMediaCount { get; }

        /// <summary>Gets the number of media items currently marked as saved successfully.</summary>
        int DownloadedMediaCount { get; }

        /// <summary>
        /// Gets the URL of the thread.
        /// </summary>
        string Url { get; }

        /// <summary>
        /// Starts one initial-and-periodic polling lifetime using the session queue.
        /// Repeated starts while active or stopping leave the existing lifetime unchanged.
        /// </summary>
        void StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval);

        /// <summary>Changes the delay for the next waiting period without interrupting current work.</summary>
        void UpdateMonitoringInterval(TimeSpan interval);

        /// <summary>
        /// Stops future checks, cancels the current provider request, and awaits its completion.
        /// Media transfer cancellation belongs to the owning session and download queue.
        /// </summary>
        Task StopMonitoringAsync();


        /// <summary>
        /// Enqueues media files in the thread for downloading.
        /// </summary>
        /// <param name="queue">The downloader queue to enqueue downloads to.</param>
        /// <param name="cancellationToken">Cancels provider requests and further media admission.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task EnqueueMediaDownloadsAsync(DownloadQueue queue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a snapshot of downloaded media identifiers, or replaces them with a copy.
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
        /// <param name="destinationPath">The verified target, or null for an unspecified legacy completion marker.</param>
        void MarkMediaAsDownloaded(string mediaIdentifier, string? destinationPath = null);

        /// <summary>Invalidates a completed-media marker when its stored file is missing or invalid.</summary>
        void MarkMediaPending(string mediaIdentifier, string? destinationPath = null);

        /// <summary>Records a media failure until a successful save of that media identifier.</summary>
        void ReportMediaFailure(string mediaIdentifier, Exception exception, string? destinationPath = null);

        /// <summary>
        /// Rechecks the thread for new posts and updates the download queue with new media.
        /// </summary>
        /// <param name="queue">The downloader queue to enqueue downloads to.</param>
        /// <param name="cancellationToken">Cancels provider requests and further media admission.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifies that the thread should be removed (e.g., when a 404 occurs or manually).
        /// </summary>
        /// <param name="abort">Indicates whether downloads should be aborted (true if manual removal).</param>
        void NotifyThreadRemoval(bool abort);
    
    }
}

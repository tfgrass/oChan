namespace oChan.Downloader;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using oChan.Interfaces;
using Serilog;

/// <summary>
/// Manages the queue of download tasks.
/// </summary>
public class DownloadQueue
{
    private readonly SemaphoreSlim _parallelDownloadsSemaphore;
    private readonly ConcurrentQueue<(DownloadItem, CancellationToken)> _downloadQueue;
    private readonly BandwidthLimiter _bandwidthLimiter;
    private readonly CancellationTokenSource _cts;
    private readonly List<Task> _workerTasks;
    private readonly HashSet<string> _queuedUrls;      // Track URLs that are enqueued
    private readonly HashSet<string> _downloadingUrls; // Track URLs that are currently being downloaded

    public int MaxParallelDownloads { get; private set; }
    public long MaxBandwidthBytesPerSecond { get; private set; }

    public DownloadQueue(int maxParallelDownloads, long maxBandwidthBytesPerSecond)
    {
        if (maxParallelDownloads <= 0)
        {
            Log.Error("Invalid MaxParallelDownloads: {MaxParallelDownloads}", maxParallelDownloads);
            throw new ArgumentException("Max parallel downloads must be greater than zero.");
        }

        if (maxBandwidthBytesPerSecond <= 0)
        {
            Log.Error("Invalid MaxBandwidthBytesPerSecond: {MaxBandwidthBytesPerSecond}", maxBandwidthBytesPerSecond);
            throw new ArgumentException("Max bandwidth must be greater than zero.");
        }

        MaxParallelDownloads = maxParallelDownloads;
        MaxBandwidthBytesPerSecond = maxBandwidthBytesPerSecond;

        _parallelDownloadsSemaphore = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);
        _downloadQueue = new ConcurrentQueue<(DownloadItem, CancellationToken)>();
        _queuedUrls = new HashSet<string>();      // Initialize the set to track enqueued URLs
        _downloadingUrls = new HashSet<string>(); // Initialize the set to track currently downloading URLs
        _bandwidthLimiter = new BandwidthLimiter(MaxBandwidthBytesPerSecond);
        _cts = new CancellationTokenSource();
        _workerTasks = new List<Task>();

        Log.Information("Initialized DownloadQueue with MaxParallelDownloads: {MaxParallelDownloads}, MaxBandwidthBytesPerSecond: {MaxBandwidthBytesPerSecond}",
            MaxParallelDownloads, MaxBandwidthBytesPerSecond);
    }

    /// <summary>
    /// Checks if a URL is already enqueued in the download queue.
    /// </summary>
    /// <param name="url">The URL of the media item.</param>
    /// <returns>True if the URL is already in the queue; otherwise, false.</returns>
    public bool IsInQueue(string url)
    {
        return _queuedUrls.Contains(url); // Check if the URL exists in the enqueued set
    }

    /// <summary>
    /// Checks if a URL is already being downloaded.
    /// </summary>
    /// <param name="url">The URL of the media item.</param>
    /// <returns>True if the URL is currently downloading; otherwise, false.</returns>
    public bool IsDownloading(string url)
    {
        return _downloadingUrls.Contains(url); // Check if the URL exists in the downloading set
    }

    /// <summary>
    /// Enqueues a download item to the queue.
    /// </summary>
    public void EnqueueDownload(DownloadItem item)
    {
        if (item == null)
        {
            Log.Error("Attempted to enqueue a null DownloadItem.");
            throw new ArgumentNullException(nameof(item));
        }

        string url = item.DownloadUri.ToString();

        // Check if the URL is either already in the queue or currently downloading
        if (IsInQueue(url) || IsDownloading(url))
        {
            Log.Warning("DownloadItem for {DownloadUri} is already in queue or downloading", url);
            return; // Skip if the item is already enqueued or downloading
        }

        var cancellationTokenSource = new CancellationTokenSource();

        // Register event listener for thread removal
        item.Thread.ThreadRemoved += (thread) =>
        {
            Log.Information("Thread {ThreadId} removed. Cancelling downloads for this thread.", thread.ThreadId);
            cancellationTokenSource.Cancel(); // Cancel all downloads for this thread
        };

        _queuedUrls.Add(url);
        _downloadingUrls.Add(url);

        _downloadQueue.Enqueue((item, cancellationTokenSource.Token));
        Log.Debug("Enqueued DownloadItem: {DownloadUri}", item.DownloadUri);
        StartWorkersIfNeeded();
    }

    /// <summary>
    /// Starts worker tasks if there are available slots.
    /// </summary>
    private void StartWorkersIfNeeded()
    {
        lock (_workerTasks)
        {
            // Start worker tasks while there are available slots and items in the queue
            while (_parallelDownloadsSemaphore.CurrentCount > 0 && !_downloadQueue.IsEmpty)
            {
                _parallelDownloadsSemaphore.Wait();

                if (_downloadQueue.TryDequeue(out (DownloadItem, CancellationToken) downloadItemPair))
                {
                    Log.Debug("Starting download task for {DownloadUri}", downloadItemPair.Item1.DownloadUri);

                    Task task = Task.Run(() => ProcessDownloadItemAsync(downloadItemPair));
                    _workerTasks.Add(task);

                    // Clean up completed tasks
                    _workerTasks.RemoveAll(t => t.IsCompleted);

                    Log.Debug("Current active worker tasks: {ActiveTasksCount}", _workerTasks.Count);
                }
                else
                {
                    Log.Debug("No DownloadItem available in queue to dequeue.");
                    _parallelDownloadsSemaphore.Release();
                }
            }
        }
    }

    /// <summary>
    /// Processes a single download item.
    /// </summary>
    private async Task ProcessDownloadItemAsync((DownloadItem, CancellationToken) downloadItemPair)
    {
        var (item, token) = downloadItemPair;
        try
        {
            Log.Information("Starting download for {DownloadUri}", item.DownloadUri);

            DownloadWorker downloadWorker = new DownloadWorker(item, _bandwidthLimiter);
            await downloadWorker.ExecuteAsync(token);

            Log.Information("Completed download for {DownloadUri}", item.DownloadUri);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Download for {DownloadUri} was cancelled.", item.DownloadUri);
        }
        catch (Exception ex)
        {
            // Handle exceptions (log, retry logic, etc.)
            Log.Error(ex, "Error downloading {DownloadUri}: {Message}", item.DownloadUri, ex.Message);
        }
        finally
        {
            // Remove the URL from the queue and downloading sets after processing
            string url = item.DownloadUri.ToString();
            _queuedUrls.Remove(url);
            _downloadingUrls.Remove(url);

            _parallelDownloadsSemaphore.Release();
            StartWorkersIfNeeded(); // Check if there are more items to process
        }
    }

    /// <summary>
    /// Cancels all downloads for the specified thread.
    /// </summary>
    /// <param name="thread">The thread for which downloads should be canceled.</param>
    public void CancelDownloadsForThread(IThread thread)
    {
        if (thread == null)
        {
            Log.Error("Attempted to cancel downloads for a null thread.");
            return;
        }

        Log.Information("Canceling all downloads for thread {ThreadId}", thread.ThreadId);

        foreach (var (item, cancellationToken) in _downloadQueue)
        {
            if (item.Thread == thread)
            {
                Log.Information("Canceling download for {DownloadUri} associated with thread {ThreadId}", item.DownloadUri, thread.ThreadId);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// Stops all downloads and cancels running tasks.
    /// </summary>
    public void StopAll()
    {
        Log.Warning("Stopping all downloads and cancelling tasks.");
        _cts.Cancel();
    }

    /// <summary>
    /// Updates the maximum number of parallel downloads.
    /// </summary>
    public void UpdateMaxParallelDownloads(int newMax)
    {
        if (newMax <= 0)
        {
            Log.Error("Attempted to set invalid MaxParallelDownloads: {NewMax}", newMax);
            throw new ArgumentException("Max parallel downloads must be greater than zero.");
        }

        Log.Information("Updating MaxParallelDownloads from {OldMax} to {NewMax}", MaxParallelDownloads, newMax);

        MaxParallelDownloads = newMax;
        _parallelDownloadsSemaphore.Release(newMax - _parallelDownloadsSemaphore.CurrentCount);
        StartWorkersIfNeeded();
    }

    /// <summary>
    /// Updates the maximum bandwidth usage.
    /// </summary>
    public void UpdateMaxBandwidth(long newMaxBytesPerSecond)
    {
        if (newMaxBytesPerSecond <= 0)
        {
            Log.Error("Attempted to set invalid MaxBandwidthBytesPerSecond: {NewMaxBytesPerSecond}", newMaxBytesPerSecond);
            throw new ArgumentException("Max bandwidth must be greater than zero.");
        }

        Log.Information("Updating MaxBandwidthBytesPerSecond from {OldMax} to {NewMax}", MaxBandwidthBytesPerSecond, newMaxBytesPerSecond);

        MaxBandwidthBytesPerSecond = newMaxBytesPerSecond;
        _bandwidthLimiter.UpdateMaxBytesPerSecond(newMaxBytesPerSecond);
    }
}

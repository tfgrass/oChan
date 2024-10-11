using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace oChan.Downloader
{
    /// <summary>
    /// Manages the queue of download tasks.
    /// </summary>
    public class DownloadQueue
    {
        private readonly SemaphoreSlim _parallelDownloadsSemaphore;
        private readonly ConcurrentQueue<DownloadItem> _downloadQueue;
        private readonly BandwidthLimiter _bandwidthLimiter;
        private readonly CancellationTokenSource _cts;
        private readonly List<Task> _workerTasks;

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
            _downloadQueue = new ConcurrentQueue<DownloadItem>();
            _bandwidthLimiter = new BandwidthLimiter(MaxBandwidthBytesPerSecond);
            _cts = new CancellationTokenSource();
            _workerTasks = new List<Task>();

            Log.Information("Initialized DownloadQueue with MaxParallelDownloads: {MaxParallelDownloads}, MaxBandwidthBytesPerSecond: {MaxBandwidthBytesPerSecond}",
                MaxParallelDownloads, MaxBandwidthBytesPerSecond);
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

            _downloadQueue.Enqueue(item);
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
                // Start worker tasks while there are slots and items
                while (_parallelDownloadsSemaphore.CurrentCount > 0 && !_downloadQueue.IsEmpty)
                {
                    _parallelDownloadsSemaphore.Wait();

                    if (_downloadQueue.TryDequeue(out var downloadItem))
                    {
                        Log.Debug("Starting download task for {DownloadUri}", downloadItem.DownloadUri);

                        var task = Task.Run(() => ProcessDownloadItemAsync(downloadItem));
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
        private async Task ProcessDownloadItemAsync(DownloadItem item)
        {
            try
            {
                Log.Information("Starting download for {DownloadUri}", item.DownloadUri);

                var downloadWorker = new DownloadWorker(item, _bandwidthLimiter);
                await downloadWorker.ExecuteAsync(_cts.Token);

                Log.Information("Completed download for {DownloadUri}", item.DownloadUri);
            }
            catch (Exception ex)
            {
                // Handle exceptions (log, retry logic, etc.)
                Log.Error(ex, "Error downloading {DownloadUri}: {Message}", item.DownloadUri, ex.Message);
            }
            finally
            {
                _parallelDownloadsSemaphore.Release();
                StartWorkersIfNeeded(); // Check if there are more items to process
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
}

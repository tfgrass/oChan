namespace oChan.Downloader;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


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
        MaxParallelDownloads = maxParallelDownloads;
        MaxBandwidthBytesPerSecond = maxBandwidthBytesPerSecond;

        _parallelDownloadsSemaphore = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);
        _downloadQueue = new ConcurrentQueue<DownloadItem>();
        _bandwidthLimiter = new BandwidthLimiter(MaxBandwidthBytesPerSecond);
        _cts = new CancellationTokenSource();
        _workerTasks = new List<Task>();
    }

    /// <summary>
    /// Enqueues a download item to the queue.
    /// </summary>
    public void EnqueueDownload(DownloadItem item)
    {
        _downloadQueue.Enqueue(item);
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
                    var task = Task.Run(() => ProcessDownloadItemAsync(downloadItem));
                    _workerTasks.Add(task);

                    // Clean up completed tasks
                    _workerTasks.RemoveAll(t => t.IsCompleted);
                }
                else
                {
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
            var downloadWorker = new DownloadWorker(item, _bandwidthLimiter);
            await downloadWorker.ExecuteAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            // Handle exceptions (log, retry logic, etc.)
            Console.WriteLine($"Error downloading {item.DownloadUri}: {ex.Message}");
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
        _cts.Cancel();
    }

    /// <summary>
    /// Updates the maximum number of parallel downloads.
    /// </summary>
    public void UpdateMaxParallelDownloads(int newMax)
    {
        if (newMax <= 0) throw new ArgumentException("Max parallel downloads must be greater than zero.");

        MaxParallelDownloads = newMax;
        _parallelDownloadsSemaphore.Release(newMax - _parallelDownloadsSemaphore.CurrentCount);
        StartWorkersIfNeeded();
    }

    /// <summary>
    /// Updates the maximum bandwidth usage.
    /// </summary>
    public void UpdateMaxBandwidth(long newMaxBytesPerSecond)
    {
        if (newMaxBytesPerSecond <= 0) throw new ArgumentException("Max bandwidth must be greater than zero.");

        MaxBandwidthBytesPerSecond = newMaxBytesPerSecond;
        _bandwidthLimiter.UpdateMaxBytesPerSecond(newMaxBytesPerSecond);
    }
}






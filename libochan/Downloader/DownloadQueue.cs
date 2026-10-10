namespace oChan.Downloader;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using oChan.Interfaces;
using oChan.Notifications;
using oChan.Storage;
using Serilog;

/// <summary>
/// Owns the session's media jobs and shared worker limit. Thread owners explicitly
/// cancel their jobs when their watch ends; the queue does not subscribe to events.
/// </summary>
public class DownloadQueue
{
    private readonly object _stateLock = new object();
    private readonly Queue<QueuedDownload> _pendingDownloads = new Queue<QueuedDownload>();
    private readonly Dictionary<JobIdentity, QueuedDownload> _jobs = new Dictionary<JobIdentity, QueuedDownload>();
    private readonly Dictionary<ThreadIdentity, ThreadState> _threads = new Dictionary<ThreadIdentity, ThreadState>();
    private readonly ConditionalWeakTable<IThread, object> _cancelledThreads = new ConditionalWeakTable<IThread, object>();
    private readonly BandwidthLimiter _bandwidthLimiter;
    private readonly MediaStore _mediaStore;
    private readonly TimeSpan _bodyIdleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CoalescedEvent<Action> _changes;
    private TaskCompletionSource _idleCompletion = CreateCompletedSource();
    private TaskCompletionSource? _stopCompletion;
    private int _maxParallelDownloads;
    private int _activeDownloads;
    private bool _stopping;

    /// <summary>Coalesced asynchronous invalidation; read a fresh snapshot when notified.</summary>
    public event Action? StateChanged
    {
        add => _changes.Add(value);
        remove => _changes.Remove(value);
    }

    public int MaxParallelDownloads
    {
        get
        {
            lock (_stateLock)
            {
                return _maxParallelDownloads;
            }
        }
    }

    public long MaxBandwidthBytesPerSecond => _bandwidthLimiter.MaxBytesPerSecond;

    /// <summary>
    /// Copies counts for the requested watch instances under one queue lock.
    /// Active includes jobs holding a worker slot, including network admission or storage work.
    /// A replacement watch with the same URL never receives its predecessor's counts.
    /// </summary>
    public IReadOnlyDictionary<IThread, DownloadCounts> GetThreadCounts(IReadOnlyList<IThread> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        Dictionary<IThread, DownloadCounts> counts = new Dictionary<IThread, DownloadCounts>(ReferenceEqualityComparer.Instance);
        foreach (IThread thread in threads)
        {
            ArgumentNullException.ThrowIfNull(thread);
            counts.TryAdd(thread, new DownloadCounts(0, 0));
        }

        lock (_stateLock)
        {
            foreach (QueuedDownload job in _jobs.Values)
            {
                IThread thread = job.Item.Thread;
                if (counts.TryGetValue(thread, out DownloadCounts previous))
                {
                    counts[thread] = job.IsActive
                        ? previous with { Active = previous.Active + 1 }
                        : previous with { Queued = previous.Queued + 1 };
                }
            }
        }

        return new ReadOnlyDictionary<IThread, DownloadCounts>(counts);
    }

    public DownloadQueue(int maxParallelDownloads, long maxBandwidthBytesPerSecond, MediaStore? mediaStore = null,
        TimeSpan? bodyIdleTimeout = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        if (maxParallelDownloads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallelDownloads), "Max parallel downloads must be greater than zero.");
        }

        if (maxBandwidthBytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBandwidthBytesPerSecond), "Max bandwidth must be greater than zero.");
        }

        _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_bodyIdleTimeout <= TimeSpan.Zero || _bodyIdleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(bodyIdleTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Serilog.Core.Logger.None;
        _changes = new CoalescedEvent<Action>(handler => handler(),
            exception => _logger.Error(exception, "Download queue observer failed"));
        _bandwidthLimiter = new BandwidthLimiter(maxBandwidthBytesPerSecond, _timeProvider, _logger);
        _mediaStore = mediaStore ?? new MediaStore();
        _maxParallelDownloads = maxParallelDownloads;
    }

    /// <summary>
    /// Diagnostic lookup including active jobs. Admission uses the complete job
    /// identity because one URL may have several legitimate destinations.
    /// </summary>
    public bool IsInQueue(string url)
    {
        lock (_stateLock)
        {
            foreach (QueuedDownload job in _jobs.Values)
            {
                if (string.Equals(job.Url, url, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsDownloading(string url)
    {
        lock (_stateLock)
        {
            foreach (QueuedDownload job in _jobs.Values)
            {
                if (job.IsActive && string.Equals(job.Url, url, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void EnqueueDownload(DownloadItem item)
    {
        TryEnqueue(item);
    }

    /// <summary>
    /// Atomically reserves a job. Returns false for a duplicate, a stopped queue,
    /// a cancelled instance or a still-active older instance of the same thread.
    /// Completed reservations are released and can be submitted again.
    /// </summary>
    public bool TryEnqueue(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Provider properties and path resolution run outside the state lock.
        ThreadIdentity threadIdentity = GetThreadIdentity(item.Thread);
        string destinationPath = Path.GetFullPath(item.DestinationPath);
        if (OperatingSystem.IsWindows())
        {
            destinationPath = destinationPath.ToUpperInvariant();
        }

        JobIdentity jobIdentity = new JobIdentity(threadIdentity, item.MediaIdentifier, destinationPath);
        string url = item.DownloadUri.ToString();
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _) || _jobs.ContainsKey(jobIdentity))
            {
                return false;
            }

            if (_threads.TryGetValue(threadIdentity, out ThreadState? threadState))
            {
                if (!ReferenceEquals(threadState.Thread, item.Thread))
                {
                    return false;
                }
            }
            else
            {
                threadState = new ThreadState(threadIdentity, item.Thread);
                _threads.Add(threadIdentity, threadState);
            }

            if (_jobs.Count == 0)
            {
                _idleCompletion = CreateCompletionSource();
            }

            QueuedDownload job = new QueuedDownload(jobIdentity, url, item, threadState);
            _jobs.Add(jobIdentity, job);
            _pendingDownloads.Enqueue(job);
            threadState.OutstandingJobs++;
            ready = ReserveWorkersLocked();
        }

        StartWorkers(ready);
        _changes.Signal();
        return true;
    }

    /// <summary>
    /// Waits for the current busy period to end without closing admission. A later
    /// submission can start another busy period after this task completes.
    /// </summary>
    public Task WaitForIdleAsync()
    {
        lock (_stateLock)
        {
            return _idleCompletion.Task;
        }
    }

    public void CancelDownloadsForThread(IThread thread)
    {
        _ = CancelDownloadsForThreadAsync(thread);
    }

    /// <summary>
    /// Closes admission for this instance, removes waiting jobs and waits for its
    /// active workers. New instances of its identity may start after those finish.
    /// </summary>
    public Task CancelDownloadsForThreadAsync(IThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ThreadIdentity identity = GetThreadIdentity(thread);
        Cleanup cleanup = new Cleanup();
        ThreadState? threadState;
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            _cancelledThreads.GetValue(thread, static (IThread _) => new object());
            if (!_threads.TryGetValue(identity, out threadState) || !ReferenceEquals(threadState.Thread, thread))
            {
                return Task.CompletedTask;
            }

            // Keep the CTS alive while cancellation callbacks execute outside the
            // lock, even if the final worker finishes at the same time.
            threadState.CancellationLeases++;
            RemovePendingJobsLocked(threadState);
            RetireThreadIfIdleLocked(threadState, cleanup);
            SignalIdleIfNeededLocked(cleanup);
            ready = ReserveWorkersLocked();
        }

        RunCleanup(cleanup);
        Task cancellation = CancelThreadStateAsync(threadState);
        StartWorkers(ready);
        _changes.Signal();
        return cancellation;
    }

    public void StopAll()
    {
        _ = StopAllAsync();
    }

    /// <summary>
    /// Permanently closes admission, cancels jobs and waits for all workers.
    /// Repeated calls return the same stop operation.
    /// </summary>
    public Task StopAllAsync()
    {
        Cleanup cleanup = new Cleanup();
        List<ThreadState> threads;
        TaskCompletionSource stopCompletion;
        lock (_stateLock)
        {
            if (_stopCompletion != null)
            {
                return _stopCompletion.Task;
            }

            _stopping = true;
            stopCompletion = CreateCompletionSource();
            _stopCompletion = stopCompletion;
            threads = new List<ThreadState>(_threads.Values);
            foreach (ThreadState threadState in threads)
            {
                threadState.CancellationLeases++;
            }

            while (_pendingDownloads.TryDequeue(out QueuedDownload? job))
            {
                _jobs.Remove(job.Identity);
                job.ThreadState.OutstandingJobs--;
            }

            foreach (ThreadState threadState in threads)
            {
                RetireThreadIfIdleLocked(threadState, cleanup);
            }

            SignalIdleIfNeededLocked(cleanup);
        }

        RunCleanup(cleanup);
        _changes.Signal();
        _ = CompleteStopAsync(threads, stopCompletion);
        return stopCompletion.Task;
    }

    public void UpdateMaxParallelDownloads(int newMax)
    {
        if (newMax <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newMax), "Max parallel downloads must be greater than zero.");
        }

        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            _maxParallelDownloads = newMax;
            ready = ReserveWorkersLocked();
        }

        StartWorkers(ready);
        _changes.Signal();
    }

    public void UpdateMaxBandwidth(long newMaxBytesPerSecond)
    {
        _bandwidthLimiter.UpdateMaxBytesPerSecond(newMaxBytesPerSecond);
    }

    private List<QueuedDownload> ReserveWorkersLocked()
    {
        List<QueuedDownload> ready = new List<QueuedDownload>();
        while (!_stopping && _activeDownloads < _maxParallelDownloads &&
               _pendingDownloads.TryDequeue(out QueuedDownload? job))
        {
            job.IsActive = true;
            _activeDownloads++;
            ready.Add(job);
        }

        return ready;
    }

    private void StartWorkers(List<QueuedDownload> ready)
    {
        foreach (QueuedDownload job in ready)
        {
            _ = Task.Run(() => ProcessDownloadItemAsync(job));
        }
    }

    private async Task ProcessDownloadItemAsync(QueuedDownload job)
    {
        try
        {
            job.ThreadState.Token.ThrowIfCancellationRequested();
            DownloadWorker worker = new DownloadWorker(job.Item, _bandwidthLimiter, _mediaStore, _bodyIdleTimeout, _timeProvider, _logger);
            await worker.ExecuteAsync(job.ThreadState.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.ThreadState.Token.IsCancellationRequested)
        {
            _logger.Debug("Download for {DownloadUri} was cancelled.", job.Item.DownloadUri);
        }
        catch (Exception exception)
        {
            try
            {
                job.Item.Thread.ReportMediaFailure(job.Item.MediaIdentifier, exception, job.Item.DestinationPath);
            }
            catch (Exception notificationException)
            {
                _logger.Error(notificationException, "Could not report media failure for {MediaId}", job.Item.MediaIdentifier);
            }
            _logger.Error(exception, "Error downloading {DownloadUri}", job.Item.DownloadUri);
        }
        finally
        {
            Cleanup cleanup = new Cleanup();
            List<QueuedDownload> ready;
            lock (_stateLock)
            {
                _activeDownloads--;
                _jobs.Remove(job.Identity);
                job.ThreadState.OutstandingJobs--;
                RetireThreadIfIdleLocked(job.ThreadState, cleanup);
                SignalIdleIfNeededLocked(cleanup);
                ready = ReserveWorkersLocked();
            }

            RunCleanup(cleanup);
            StartWorkers(ready);
            _changes.Signal();
        }
    }

    private void RemovePendingJobsLocked(ThreadState threadState)
    {
        int pendingCount = _pendingDownloads.Count;
        for (int index = 0; index < pendingCount; index++)
        {
            QueuedDownload job = _pendingDownloads.Dequeue();
            if (ReferenceEquals(job.ThreadState, threadState))
            {
                _jobs.Remove(job.Identity);
                threadState.OutstandingJobs--;
            }
            else
            {
                _pendingDownloads.Enqueue(job);
            }
        }
    }

    private async Task CancelThreadStateAsync(ThreadState threadState)
    {
        try
        {
            try
            {
                await threadState.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Error in cancellation callback for thread {ThreadId}", threadState.Identity.ThreadId);
            }

            await threadState.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            Cleanup cleanup = new Cleanup();
            lock (_stateLock)
            {
                threadState.CancellationLeases--;
                ReleaseCancellationIfUnusedLocked(threadState, cleanup);
            }

            RunCleanup(cleanup);
        }
    }

    private async Task CompleteStopAsync(List<ThreadState> threads, TaskCompletionSource completion)
    {
        try
        {
            List<Task> cancellations = new List<Task>(threads.Count);
            foreach (ThreadState threadState in threads)
            {
                cancellations.Add(CancelThreadStateAsync(threadState));
            }

            await Task.WhenAll(cancellations).ConfigureAwait(false);
            await WaitForIdleAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void RetireThreadIfIdleLocked(ThreadState threadState, Cleanup cleanup)
    {
        if (threadState.OutstandingJobs != 0 || threadState.IsRetired)
        {
            return;
        }

        threadState.IsRetired = true;
        _threads.Remove(threadState.Identity);
        cleanup.Completions.Add(threadState.Completion);
        ReleaseCancellationIfUnusedLocked(threadState, cleanup);
    }

    private static void ReleaseCancellationIfUnusedLocked(ThreadState threadState, Cleanup cleanup)
    {
        if (threadState.OutstandingJobs == 0 && threadState.CancellationLeases == 0 && !threadState.IsDisposed)
        {
            threadState.IsDisposed = true;
            cleanup.Cancellations.Add(threadState.Cancellation);
        }
    }

    private void SignalIdleIfNeededLocked(Cleanup cleanup)
    {
        if (_jobs.Count == 0)
        {
            cleanup.Completions.Add(_idleCompletion);
        }
    }

    private static void RunCleanup(Cleanup cleanup)
    {
        foreach (CancellationTokenSource cancellation in cleanup.Cancellations)
        {
            cancellation.Dispose();
        }

        foreach (TaskCompletionSource completion in cleanup.Completions)
        {
            completion.TrySetResult();
        }
    }

    private static ThreadIdentity GetThreadIdentity(IThread thread)
    {
        return new ThreadIdentity(thread.Board.ImageBoard.Name, thread.Board.BoardCode, thread.ThreadId);
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        TaskCompletionSource completion = CreateCompletionSource();
        completion.SetResult();
        return completion;
    }

    private readonly record struct ThreadIdentity(string Source, string Board, string ThreadId);
    private readonly record struct JobIdentity(ThreadIdentity Thread, string MediaId, string DestinationPath);

    private sealed class QueuedDownload
    {
        public JobIdentity Identity { get; }
        public string Url { get; }
        public DownloadItem Item { get; }
        public ThreadState ThreadState { get; }
        public bool IsActive { get; set; }

        public QueuedDownload(JobIdentity identity, string url, DownloadItem item, ThreadState threadState)
        {
            Identity = identity;
            Url = url;
            Item = item;
            ThreadState = threadState;
        }
    }

    private sealed class ThreadState
    {
        public ThreadIdentity Identity { get; }
        public IThread Thread { get; }
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
        public CancellationToken Token { get; }
        public TaskCompletionSource Completion { get; } = CreateCompletionSource();
        public int OutstandingJobs { get; set; }
        public int CancellationLeases { get; set; }
        public bool IsRetired { get; set; }
        public bool IsDisposed { get; set; }

        public ThreadState(ThreadIdentity identity, IThread thread)
        {
            Identity = identity;
            Thread = thread;
            Token = Cancellation.Token;
        }
    }

    private sealed class Cleanup
    {
        public List<CancellationTokenSource> Cancellations { get; } = new List<CancellationTokenSource>();
        public List<TaskCompletionSource> Completions { get; } = new List<TaskCompletionSource>();
    }
}

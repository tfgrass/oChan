namespace oChan.Boards.Base;

using System;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Http;
using oChan.Storage;
using oChan.Notifications;
using Serilog;

/// <summary>
/// Passive thread state. The session explicitly starts and stops its polling lifetime.
/// Property notifications are asynchronous, coalesced invalidations. UI clients must dispatch them.
/// </summary>
public abstract class BaseThread : IThread
{
    private readonly object _monitoringLock = new object();
    private readonly object _stateLock = new object();
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private TimeSpan _monitoringInterval = TimeSpan.FromSeconds(60);
    private HashSet<string> _downloadedMedia = new HashSet<string>(StringComparer.Ordinal);
    private static readonly StringComparer DestinationComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, HashSet<string>> _verifiedDestinations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, FailureInfo>> _destinationFailures = new Dictionary<string, Dictionary<string, FailureInfo>>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mediaFailures = new Dictionary<string, string>(StringComparer.Ordinal);
    private ThreadSourceState _sourceState = ThreadSourceState.Active;
    private RequestPolicyProgress? _requestProgress;
    private string _title = "Unknown";
    private ThreadActivity _activity = ThreadActivity.Pending;
    private FailureInfo? _lastCheckFailure;
    private readonly CoalescedEvent<PropertyChangedEventHandler> _propertyChanges;
    private readonly SingleOwnerCallback<Action<IThread>> _removal = new SingleOwnerCallback<Action<IThread>>();
    private int _totalMediaCount;

    protected BaseThread()
    {
        _propertyChanges = new CoalescedEvent<PropertyChangedEventHandler>(
            handler => handler(this, new PropertyChangedEventArgs(string.Empty)),
            exception => Logger.Error(exception, "Thread property observer failed for {ThreadId}", ThreadId));
    }

    protected ILogger Logger => Board.ImageBoard.Logger;
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _propertyChanges.Add(value);
        remove => _propertyChanges.Remove(value);
    }
    public IDisposable RegisterRemovalSink(Action<IThread> sink) => _removal.Register(sink);
    public FailureInfo? LastCheckFailure { get { lock (_stateLock) { return _lastCheckFailure; } } }

    public ThreadSnapshot GetSnapshot(DownloadCounts downloads)
    {
        lock (_stateLock)
        {
            Dictionary<string, FailureInfo> failures = new Dictionary<string, FailureInfo>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, FailureInfo>> entry in _destinationFailures)
                failures.Add(entry.Key, entry.Value.Values.Last());
            return new ThreadSnapshot(new ResourceId(Board.ImageBoard.Name, Board.BoardCode, ThreadId),
                Url, _title, DownloadDirectory, _activity, _sourceState, _requestProgress,
                _totalMediaCount, _downloadedMedia.Count,
                new ReadOnlyDictionary<string, FailureInfo>(failures), _lastCheckFailure, downloads);
        }
    }

    private void SetCheckFailure(FailureInfo? failure)
    {
        lock (_stateLock) { _lastCheckFailure = failure; }
        OnPropertyChanged(nameof(LastCheckFailure));
    }

    public abstract IBoard Board { get; }
    public abstract string ThreadId { get; }
    public abstract string NiceName { get; }
    public abstract Uri ThreadUri { get; }
    public string Url => ThreadUri.ToString();
    public string DownloadDirectory => StorageLayout.GetThreadDirectory(OutputDirectory, Board.ImageBoard.Name, Board.BoardCode, ThreadId);
    protected string OutputDirectory { get; private set; } = "Downloads";
    protected virtual TimeSpan MinimumRecheckInterval => TimeSpan.FromMilliseconds(1);

    public ThreadSourceState SourceState
    {
        get { lock (_stateLock) { return _sourceState; } }
    }

    public RequestPolicyProgress? RequestProgress
    {
        get { lock (_stateLock) { return _requestProgress; } }
    }

    /// <summary>Providers clear this in finally after request and body processing finish.</summary>
    protected void SetRequestProgress(RequestPolicyProgress? progress)
    {
        lock (_stateLock)
        {
            if (_requestProgress == progress) return;
            _requestProgress = progress;
        }
        OnPropertyChanged(nameof(RequestProgress));
    }

    public IReadOnlyDictionary<string, string> MediaFailures
    {
        get
        {
            lock (_stateLock)
            {
                return new Dictionary<string, string>(_mediaFailures, StringComparer.Ordinal);
            }
        }
    }

    public virtual string Title
    {
        get { lock (_stateLock) { return _title; } }
        set
        {
            lock (_stateLock)
            {
                if (_title == value) return;
                _title = value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(NiceName));
        }
    }

    public string Status
    {
        get
        {
            ThreadActivity activity = Activity;
            return activity switch
            {
                ThreadActivity.NoMedia => "No media found",
                ThreadActivity.NotFound => "Not found",
                _ => activity.ToString()
            };
        }
    }

    public ThreadActivity Activity
    {
        get { lock (_stateLock) { return _activity; } }
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_stateLock)
            {
                if (_mediaFailures.Count > 0 && value is ThreadActivity.Finished or ThreadActivity.Downloading or ThreadActivity.Pending)
                    value = ThreadActivity.Error;
                if (_activity == value) return;
                _activity = value;
            }
            OnPropertyChanged();
            Logger.Information("Thread {ThreadId} activity changed to: {Activity}", ThreadId, value);
        }
    }

    public int TotalMediaCount
    {
        get { lock (_stateLock) { return _totalMediaCount; } }
        set
        {
            lock (_stateLock)
            {
                if (_totalMediaCount == value) return;
                _totalMediaCount = value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
        }
    }

    public int DownloadedMediaCount
    {
        get { lock (_stateLock) { return _downloadedMedia.Count; } }
    }

    public string Progress
    {
        get
        {
            lock (_stateLock)
            {
                if (_totalMediaCount == 0) return "0 / 0 (0%)";
                double percent = (double)_downloadedMedia.Count / _totalMediaCount * 100;
                return $"{_downloadedMedia.Count} / {_totalMediaCount} ({percent:0.##}%)";
            }
        }
    }

    /// <summary>Gets a snapshot, or replaces the completed-media set with a copy.</summary>
    public HashSet<string> DownloadedMedia
    {
        get
        {
            lock (_stateLock)
            {
                return new HashSet<string>(_downloadedMedia, StringComparer.Ordinal);
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            HashSet<string> snapshot = new HashSet<string>(value, StringComparer.Ordinal);
            lock (_stateLock)
            {
                _downloadedMedia = snapshot;
                _verifiedDestinations.Clear();
                foreach (string mediaIdentifier in snapshot)
                    _verifiedDestinations[mediaIdentifier] = new HashSet<string>(DestinationComparer) { string.Empty };
            }
            OnPropertyChanged(nameof(DownloadedMedia));
            OnPropertyChanged(nameof(DownloadedMediaCount));
            OnPropertyChanged(nameof(Progress));
        }
    }

    public void StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));

        lock (_monitoringLock)
        {
            if (_monitoringTask != null) return;
            OutputDirectory = outputDirectory;
            _monitoringInterval = interval;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            _monitoringCancellation = cancellation;
            _monitoringTask = Task.Run(() => MonitorAsync(queue, token));
        }
    }

    public void UpdateMonitoringInterval(TimeSpan interval)
    {
        if (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_monitoringLock) { _monitoringInterval = interval; }
    }

    public async Task StopMonitoringAsync()
    {
        Task? monitoringTask;
        CancellationTokenSource? cancellation;
        lock (_monitoringLock)
        {
            monitoringTask = _monitoringTask;
            cancellation = _monitoringCancellation;
        }
        RequestStop(cancellation);
        if (monitoringTask == null) return;
        await monitoringTask.ConfigureAwait(false);
        lock (_monitoringLock)
        {
            if (ReferenceEquals(_monitoringTask, monitoringTask))
            {
                _monitoringTask = null;
                _monitoringCancellation = null;
                cancellation?.Dispose();
            }
        }
    }

    private async Task MonitorAsync(DownloadQueue queue, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && SourceState == ThreadSourceState.Active)
            {
                try
                {
                    await checkThreadAsync(queue, cancellationToken).ConfigureAwait(false);
                    SetCheckFailure(null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Activity = ThreadActivity.Error;
                    SetCheckFailure(FailureInfo.FromException(exception, media: false));
                    Logger.Error(exception, "Error checking thread {ThreadId}", ThreadId);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (SourceState != ThreadSourceState.Active) return;
                TimeSpan interval;
                lock (_monitoringLock) { interval = _monitoringInterval; }
                TimeSpan minimumInterval = MinimumRecheckInterval;
                if (interval < minimumInterval) interval = minimumInterval;
                await WaitForNextCheckAsync(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A requested stop cancels both the provider request and the polling delay.
        }
        catch (Exception exception)
        {
            Activity = ThreadActivity.Error;
            SetCheckFailure(FailureInfo.FromException(exception, media: false));
            Logger.Error(exception, "Monitoring stopped unexpectedly for thread {ThreadId}", ThreadId);
        }
    }

    protected virtual Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        return Task.Delay(interval, cancellationToken);
    }

    /// <summary>Ends source polling after the current snapshot without stopping queued media jobs.</summary>
    protected void MarkPollingTerminal(ThreadSourceState state)
    {
        if (state != ThreadSourceState.Archived && state != ThreadSourceState.NotFound)
            throw new ArgumentOutOfRangeException(nameof(state));
        lock (_stateLock)
        {
            if (_sourceState == state) return;
            _sourceState = state;
        }
        OnPropertyChanged(nameof(SourceState));
    }


    public virtual Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        cancellationToken.ThrowIfCancellationRequested();
        Activity = ThreadActivity.Checking;
        return Task.CompletedTask;
    }

    public virtual Task EnqueueMediaDownloadsAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        return checkThreadAsync(queue, cancellationToken);
    }

    public bool IsMediaDownloaded(string mediaIdentifier)
    {
        lock (_stateLock) { return _downloadedMedia.Contains(mediaIdentifier); }
    }

    public void MarkMediaAsDownloaded(string mediaIdentifier, string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentifier);
        string destinationKey = GetDestinationKey(destinationPath);
        bool mediaChanged;
        bool failuresChanged;
        bool statusChanged;
        lock (_stateLock)
        {
            if (!_verifiedDestinations.TryGetValue(mediaIdentifier, out HashSet<string>? destinations))
            {
                destinations = new HashSet<string>(DestinationComparer);
                _verifiedDestinations.Add(mediaIdentifier, destinations);
            }
            destinations.Add(destinationKey);
            mediaChanged = _downloadedMedia.Add(mediaIdentifier);
            failuresChanged = _destinationFailures.TryGetValue(mediaIdentifier, out Dictionary<string, FailureInfo>? failures)
                && failures.Remove(destinationKey);
            if (failuresChanged) RefreshMediaFailureLocked(mediaIdentifier);
            if (!mediaChanged && !failuresChanged) return;
            ThreadActivity nextStatus = _activity;
            if (_mediaFailures.Count > 0) nextStatus = ThreadActivity.Error;
            else if (_downloadedMedia.Count == _totalMediaCount) nextStatus = ThreadActivity.Finished;
            else if (failuresChanged && _activity == ThreadActivity.Error) nextStatus = ThreadActivity.Downloading;
            statusChanged = _activity != nextStatus;
            _activity = nextStatus;
        }
        NotifyMediaStateChanged(mediaChanged, failuresChanged, statusChanged);
    }

    public void MarkMediaPending(string mediaIdentifier, string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentifier);
        string destinationKey = GetDestinationKey(destinationPath);
        bool statusChanged;
        lock (_stateLock)
        {
            if (!InvalidateDestinationLocked(mediaIdentifier, destinationKey, destinationPath == null)) return;
            statusChanged = _activity == ThreadActivity.Finished;
            if (statusChanged) _activity = _mediaFailures.Count > 0 ? ThreadActivity.Error : ThreadActivity.Pending;
        }
        NotifyMediaStateChanged(true, false, statusChanged);
    }

    public void ReportMediaFailure(string mediaIdentifier, Exception exception, string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentifier);
        ArgumentNullException.ThrowIfNull(exception);
        string destinationKey = GetDestinationKey(destinationPath);
        bool mediaChanged;
        bool statusChanged;
        lock (_stateLock)
        {
            mediaChanged = InvalidateDestinationLocked(mediaIdentifier, destinationKey, destinationPath == null);
            if (!_destinationFailures.TryGetValue(mediaIdentifier, out Dictionary<string, FailureInfo>? failures))
            {
                failures = new Dictionary<string, FailureInfo>(DestinationComparer);
                _destinationFailures.Add(mediaIdentifier, failures);
            }
            failures[destinationKey] = FailureInfo.FromException(exception, media: true);
            RefreshMediaFailureLocked(mediaIdentifier);
            statusChanged = _activity != ThreadActivity.Error;
            _activity = ThreadActivity.Error;
        }
        NotifyMediaStateChanged(mediaChanged, true, statusChanged);
    }

    // The empty key represents an old completion/failure without a known destination.
    private static string GetDestinationKey(string? destinationPath)
    {
        if (destinationPath == null) return string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return Path.GetFullPath(destinationPath);
    }

    private bool InvalidateDestinationLocked(string mediaIdentifier, string destinationKey, bool allDestinations)
    {
        if (!allDestinations && _verifiedDestinations.TryGetValue(mediaIdentifier, out HashSet<string>? destinations))
        {
            destinations.Remove(destinationKey);
            // An unspecified old marker cannot prove that a different destination is intact.
            destinations.Remove(string.Empty);
            if (destinations.Count > 0) return false;
        }
        _verifiedDestinations.Remove(mediaIdentifier);
        return _downloadedMedia.Remove(mediaIdentifier);
    }

    private void RefreshMediaFailureLocked(string mediaIdentifier)
    {
        Dictionary<string, FailureInfo> failures = _destinationFailures[mediaIdentifier];
        if (failures.Count == 0)
        {
            _destinationFailures.Remove(mediaIdentifier);
            _mediaFailures.Remove(mediaIdentifier);
            return;
        }
        _mediaFailures[mediaIdentifier] = string.Join("; ", failures.Values.Select(failure => failure.Message));
    }

    private void NotifyMediaStateChanged(bool mediaChanged, bool failuresChanged, bool statusChanged)
    {
        if (mediaChanged)
        {
            OnPropertyChanged(nameof(DownloadedMedia));
            OnPropertyChanged(nameof(DownloadedMediaCount));
            OnPropertyChanged(nameof(Progress));
        }
        if (failuresChanged) OnPropertyChanged(nameof(MediaFailures));
        if (statusChanged) OnPropertyChanged(nameof(Status));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => _propertyChanges.Signal();

    public void NotifyThreadRemoval(bool abort)
    {
        CancellationTokenSource? cancellation;
        lock (_monitoringLock) { cancellation = _monitoringCancellation; }
        RequestStop(cancellation);
        _removal.Current?.Invoke(this);
    }

    private static void RequestStop(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A concurrent stop already drained this lifetime and released its token source.
        }
    }
}

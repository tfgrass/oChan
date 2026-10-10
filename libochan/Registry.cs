namespace oChan;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;
using oChan.Storage;
using oChan.Notifications;
using Serilog;

public enum AdmissionStatus { Added, AlreadyPresent, Invalid, Unsupported }
public sealed record AdmissionResult(AdmissionStatus Status, IThread? Thread = null, IBoard? Board = null);

/// <summary>Owns admission, durable session intent, watch lifetimes and the shared queue. Construction is passive.</summary>
public sealed class Registry : IAsyncDisposable
{
    private sealed record ResourceKey(string Source, string Board, string? Thread = null);
    private sealed record Resource(ResourceKey Key, string Url, IThread? Thread, IBoard? Board);
    private sealed class Watch
    {
        public IThread? Thread { get; init; }
        public IBoard? Board { get; init; }
        public required string OutputRoot { get; init; }
        public bool Stopping { get; set; }
        public bool Ready { get; set; }
        public TaskCompletionSource Stopped { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? OwnerRegistration { get; set; }
        public PropertyChangedEventHandler? StateHandler { get; set; }
        public int RemovalRequested;
    }

    private readonly IImageBoard[] _providers;
    private readonly ILogger _logger;
    private readonly CoalescedEvent<Action> _stateChanges;
    private readonly Queue<string> _warnings = new Queue<string>();
    private readonly DownloadQueue _queue;
    private readonly ISessionStore? _sessionStore;
    private string _outputDirectory;
    private TimeSpan _threadInterval;
    private TimeSpan _boardInterval;
    private bool _persistSession;
    private bool _restoreRequired;
    private SessionSnapshot _session = new SessionSnapshot();
    private bool _restored;
    private bool _hasMutations;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly object _state = new object();
    private readonly Dictionary<ResourceKey, Watch> _watches = new Dictionary<ResourceKey, Watch>();
    private readonly HashSet<ResourceKey> _suppressed = new HashSet<ResourceKey>();
    private readonly HashSet<Task> _backgroundTasks = new HashSet<Task>();
    private readonly object _shutdownLock = new object();
    private bool _stopping;
    private Task? _shutdown;

    /// <summary>Asynchronous coalesced invalidation. Subscribe first, then read GetSnapshot; intermediate transitions may coalesce.</summary>
    public event Action? StateChanged
    {
        add => _stateChanges.Add(value);
        remove => _stateChanges.Remove(value);
    }

    public DownloadSessionSnapshot GetSnapshot()
    {
        RegistryView view = GetView();
        return new DownloadSessionSnapshot(Array.AsReadOnly(view.Threads.Values.ToArray()),
            Array.AsReadOnly(view.Boards.Values.ToArray()), view.IsStopping, view.Warnings);
    }

    /// <summary>Captures exact resource handles with their copied state, for in-process views and removal commands.</summary>
    public RegistryView GetView()
    {
        Watch[] watches;
        bool stopping;
        string[] warnings;
        lock (_state)
        {
            watches = _watches.Values.Where(watch => watch.Ready && !watch.Stopping).ToArray();
            stopping = _stopping;
            warnings = _warnings.ToArray();
        }
        IThread[] threads = watches.Where(watch => watch.Thread != null).Select(watch => watch.Thread!).ToArray();
        IReadOnlyDictionary<IThread, DownloadCounts> counts = _queue.GetThreadCounts(threads);
        Dictionary<IThread, ThreadSnapshot> threadSnapshots = new Dictionary<IThread, ThreadSnapshot>(ReferenceEqualityComparer.Instance);
        Dictionary<IBoard, BoardSnapshot> boardSnapshots = new Dictionary<IBoard, BoardSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (IThread thread in threads) threadSnapshots.Add(thread, thread.GetSnapshot(counts[thread]));
        foreach (Watch watch in watches)
        {
            if (watch.Board == null) continue;
            string directory = StorageLayout.GetBoardDirectory(watch.OutputRoot, watch.Board.ImageBoard.Name, watch.Board.BoardCode);
            boardSnapshots.Add(watch.Board, watch.Board.GetSnapshot(directory));
        }
        return new RegistryView(new ReadOnlyDictionary<IThread, ThreadSnapshot>(threadSnapshots),
            new ReadOnlyDictionary<IBoard, BoardSnapshot>(boardSnapshots), stopping, Array.AsReadOnly(warnings));
    }

    public IReadOnlyList<IThread> Threads
    {
        get { lock (_state) { return _watches.Values.Where(w => w.Thread != null && w.Ready && !w.Stopping).Select(w => w.Thread!).ToArray(); } }
    }
    public IReadOnlyList<IBoard> Boards
    {
        get { lock (_state) { return _watches.Values.Where(w => w.Board != null && w.Ready && !w.Stopping).Select(w => w.Board!).ToArray(); } }
    }

    public Registry(IEnumerable<IImageBoard> providers, DownloadQueue queue, string outputDirectory,
        TimeSpan threadInterval, int boardIntervalSeconds, ISessionStore? sessionStore = null, bool persistSession = true, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (threadInterval.TotalMilliseconds < 1 || threadInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(threadInterval));
        if (boardIntervalSeconds <= 0 || TimeSpan.FromSeconds(boardIntervalSeconds).TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(boardIntervalSeconds));
        _providers = providers.ToArray();
        if (_providers.Any(p => p == null) || _providers.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != _providers.Length)
            throw new ArgumentException("Providers must have distinct, non-null source identities.", nameof(providers));
        string resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
        _logger = logger ?? Serilog.Core.Logger.None;
        _stateChanges = new CoalescedEvent<Action>(handler => handler(),
            exception => _logger.Error(exception, "Session observer failed"));
        _queue = queue;
        _queue.StateChanged += SignalChanged;
        _outputDirectory = resolvedOutputDirectory;
        _threadInterval = threadInterval;
        _boardInterval = TimeSpan.FromSeconds(boardIntervalSeconds);
        _sessionStore = sessionStore;
        _persistSession = persistSession;
        _restoreRequired = sessionStore != null && persistSession;
    }

    public string GetDownloadDirectory(IBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        lock (_state)
        {
            if (!_watches.TryGetValue(Key(board), out Watch? watch) || !ReferenceEquals(watch.Board, board))
                throw new InvalidOperationException("The board is not registered with this session.");
            return StorageLayout.GetBoardDirectory(watch.OutputRoot, board.ImageBoard.Name, board.BoardCode);
        }
    }

    public async Task<AdmissionResult> AddAsync(string url)
    {
        while (true)
        {
            Task? oldLifetime = null;
            AdmissionResult? result = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                EnsureRestoredForPersistence();
                AdmissionStatus status = Resolve(url, out Resource? resource);
                if (resource == null) return new AdmissionResult(status);
                Watch? current;
                lock (_state) { _watches.TryGetValue(resource.Key, out current); }
                if (current != null)
                {
                    if (current.Stopping) oldLifetime = current.Stopped.Task;
                    else result = new AdmissionResult(AdmissionStatus.AlreadyPresent, current.Thread, current.Board);
                }
                else
                {
                    result = await AdmitAsync(resource, _outputDirectory, persist: true).ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
            if (oldLifetime != null) { await oldLifetime.ConfigureAwait(false); continue; }
            if (result!.Status == AdmissionStatus.Added) SignalChanged();
            return result;
        }
    }

    /// <summary>Explicitly restores before user admissions. No partial restore snapshot is saved.</summary>
    public async Task RestoreAsync()
    {
        List<AdmissionResult> added = new List<AdmissionResult>();
        List<string> warnings = new List<string>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_restored) return;
            if (_hasMutations) throw new InvalidOperationException("Restore the session before adding or removing watches.");
            if (_sessionStore == null) { _restored = true; return; }
            SessionLoadResult loaded = await _sessionStore.LoadAsync().ConfigureAwait(false);
            if (loaded.Warning != null) warnings.Add(loaded.Warning);
            Dictionary<ResourceKey, Resource> resources = new Dictionary<ResourceKey, Resource>();
            Dictionary<string, string> canonicalUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> threads = NormalizeRestoredUrls(loaded.Snapshot.ThreadUrls, true, resources, canonicalUrls, warnings);
            HashSet<string> boards = NormalizeRestoredUrls(loaded.Snapshot.BoardUrls, false, resources, canonicalUrls, warnings);
            HashSet<string> suppressedUrls = new HashSet<string>(StringComparer.Ordinal);
            _suppressed.Clear();
            foreach (string url in loaded.Snapshot.SuppressedThreadUrls)
            {
                try
                {
                    AdmissionStatus status = Resolve(url, out Resource? resource);
                    if (resource?.Thread != null)
                    {
                        _suppressed.Add(resource.Key);
                        suppressedUrls.Add(resource.Url);
                        canonicalUrls[url] = resource.Url;
                    }
                    else { suppressedUrls.Add(url); warnings.Add($"Could not restore suppressed thread '{url}' ({status}); its saved entry was retained."); }
                }
                catch (Exception exception) { suppressedUrls.Add(url); warnings.Add($"Could not resolve suppressed thread '{url}': {exception.Message}"); }
            }
            Dictionary<string, string> roots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in loaded.Snapshot.DownloadDirectories)
            {
                string canonical = canonicalUrls.TryGetValue(entry.Key, out string? mapped) ? mapped : entry.Key;
                roots[canonical] = entry.Value;
            }
            // Install every assignment before any provider starts and produces discoveries.
            foreach (Resource resource in resources.Values.Where(r => r.Board != null))
                roots.TryAdd(resource.Url, _outputDirectory);
            foreach (Resource resource in resources.Values.Where(r => r.Thread != null))
            {
                string boardUrl = resource.Thread!.Board.BoardUri.AbsoluteUri;
                string inherited = roots.TryGetValue(boardUrl, out string? boardRoot) ? boardRoot : _outputDirectory;
                roots.TryAdd(resource.Url, inherited);
            }
            SessionSnapshot normalized = new SessionSnapshot
            {
                ThreadUrls = threads.ToArray(), BoardUrls = boards.ToArray(),
                SuppressedThreadUrls = suppressedUrls.ToArray(), DownloadDirectories = roots
            };
            bool missingAssignments = resources.Values.Any(resource =>
                !loaded.Snapshot.DownloadDirectories.TryGetValue(resource.Url, out string? savedRoot) || savedRoot != roots[resource.Url]);
            if (_persistSession && !loaded.Recovered && missingAssignments)
            {
                // Migrate the entire valid legacy snapshot before activation, preserving
                // unsupported entries and suppressions. Recovery documents remain untouched.
                await _sessionStore.SaveAsync(CopySnapshot(normalized)).ConfigureAwait(false);
            }
            _session = normalized;
            _restored = true;
            _restoreRequired = false;
            foreach (Resource resource in resources.Values)
            {
                if (resource.Thread != null && _suppressed.Contains(resource.Key)) continue;
                try { added.Add(await AdmitAsync(resource, roots[resource.Url], persist: false).ConfigureAwait(false)); }
                catch (Exception exception) { warnings.Add($"Could not start restored watch '{resource.Url}': {exception.Message}. Its saved entry was retained."); }
            }
        }
        finally { _gate.Release(); }
        foreach (string warning in warnings) Warn(warning);
        if (added.Count > 0 || warnings.Count > 0) SignalChanged();
    }

    public async Task ApplyOptionsAsync(SessionOptions options, bool persistSession, Action? commitSettings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        string nextRoot = Path.GetFullPath(options.DownloadPath);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (persistSession && _restoreRequired && !_restored)
                throw new InvalidOperationException("Call RestoreAsync before applying persistent session options.");
            SessionSnapshot? previousDurable = null;
            if (persistSession && _sessionStore != null)
            {
                if (commitSettings != null) previousDurable = (await _sessionStore.LoadAsync().ConfigureAwait(false)).Snapshot;
                await _sessionStore.SaveAsync(CopySnapshot(_session)).ConfigureAwait(false);
            }
            try { commitSettings?.Invoke(); }
            catch (Exception commitFailure)
            {
                if (previousDurable != null)
                {
                    try { await _sessionStore!.SaveAsync(previousDurable).ConfigureAwait(false); }
                    catch (Exception rollbackFailure)
                    {
                        throw new AggregateException("Settings could not be committed and session rollback also failed.", commitFailure, rollbackFailure);
                    }
                }
                throw;
            }
            _queue.UpdateMaxParallelDownloads(options.MaxParallelDownloads);
            _queue.UpdateMaxBandwidth(options.BandwidthBytesPerSecond);
            Watch[] active;
            lock (_state) { active = _watches.Values.Where(w => !w.Stopping).ToArray(); }
            foreach (Watch watch in active)
            {
                if (watch.Thread != null) watch.Thread.UpdateMonitoringInterval(options.RecheckInterval);
                else watch.Board!.UpdateMonitoringInterval(options.RecheckInterval);
            }
            _outputDirectory = nextRoot;
            _threadInterval = options.RecheckInterval;
            _boardInterval = options.RecheckInterval;
            _persistSession = persistSession;
            if (!persistSession) _restoreRequired = false;
        }
        finally { _gate.Release(); }
    }

    // Caller holds the gate. Durable intent precedes activation and public success.
    private async Task<AdmissionResult> AdmitAsync(Resource resource, string outputRoot, bool persist)
    {
        SessionSnapshot previous = _session;
        bool previouslySuppressed = _suppressed.Contains(resource.Key);
        if (persist)
        {
            SessionSnapshot next = AdmissionSnapshot(resource, outputRoot);
            await SaveSessionAsync(next).ConfigureAwait(false);
            _session = next;
            _hasMutations = true;
            if (resource.Thread != null) _suppressed.Remove(resource.Key);
        }
        Watch watch = new Watch { Thread = resource.Thread, Board = resource.Board, OutputRoot = outputRoot };
        lock (_state) { _watches.Add(resource.Key, watch); }
        try
        {
            if (resource.Thread != null)
            {
                watch.OwnerRegistration = resource.Thread.RegisterRemovalSink(removed =>
                {
                    if (Interlocked.Exchange(ref watch.RemovalRequested, 1) == 0)
                        Track(async () =>
                        {
                            try { await RemoveThreadAsync(removed).ConfigureAwait(false); }
                            finally { Volatile.Write(ref watch.RemovalRequested, 0); }
                        });
                });
                watch.StateHandler = (_, _) => SignalChanged();
                resource.Thread.PropertyChanged += watch.StateHandler;
                resource.Thread.StartMonitoring(_queue, watch.OutputRoot, _threadInterval);
            }
            else
            {
                watch.OwnerRegistration = resource.Board!.RegisterDiscoverySink((thread, token) =>
                    new ValueTask(AdmitDiscoveryAsync(watch, thread, token)));
                watch.StateHandler = (_, _) => SignalChanged();
                resource.Board.PropertyChanged += watch.StateHandler;
                resource.Board.StartMonitoring((int)_boardInterval.TotalSeconds);
                resource.Board.UpdateMonitoringInterval(_boardInterval);
            }
            lock (_state) { watch.Ready = true; }
            return new AdmissionResult(AdmissionStatus.Added, resource.Thread, resource.Board);
        }
        catch (Exception setupFailure)
        {
            List<Exception> failures = new List<Exception> { setupFailure };
            DetachObservers(watch);
            try
            {
                // A rejected second owner must not stop the first session's resource.
                if (watch.OwnerRegistration != null) await StopWatchAsync(watch).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            finally
            {
                watch.OwnerRegistration?.Dispose();
                lock (_state) { _watches.Remove(resource.Key); }
                watch.Stopped.TrySetResult();
            }
            if (persist)
            {
                try
                {
                    await SaveSessionAsync(previous).ConfigureAwait(false);
                    _session = previous;
                    if (previouslySuppressed) _suppressed.Add(resource.Key);
                }
                catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            }
            if (failures.Count > 1) throw new AggregateException("Watch setup failed and cleanup or session rollback also failed.", failures);
            throw;
        }
    }

    private async Task AdmitDiscoveryAsync(Watch parent, IThread thread, CancellationToken cancellationToken)
    {
        AdmissionResult? result = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping || parent.Stopping) return;
            ResourceKey boardKey = Key(parent.Board!);
            ResourceKey threadKey = Key(thread);
            lock (_state)
            {
                if (!_watches.TryGetValue(boardKey, out Watch? current) || !ReferenceEquals(current, parent) || _watches.ContainsKey(threadKey)) return;
            }
            if (_suppressed.Contains(threadKey)) return;
            Resource resource = new Resource(threadKey, thread.ThreadUri.AbsoluteUri, thread, null);
            result = await AdmitAsync(resource, parent.OutputRoot, persist: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Warn($"Session change failed: {exception.Message}");
            throw;
        }
        finally { _gate.Release(); }
        if (result != null) SignalChanged();
    }

    public Task RemoveThreadAsync(IThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return RemoveAsync(Key(thread), thread, null);
    }
    public Task RemoveBoardAsync(IBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return RemoveAsync(Key(board), null, board);
    }

    private async Task RemoveAsync(ResourceKey key, IThread? thread, IBoard? board, bool shutdown = false)
    {
        Watch? watch;
        Task? alreadyStopping = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping && !shutdown, this);
            lock (_state)
            {
                if (!_watches.TryGetValue(key, out watch) || !ReferenceEquals(watch.Thread, thread) || !ReferenceEquals(watch.Board, board)) return;
                if (watch.Stopping) alreadyStopping = watch.Stopped.Task;
            }
            if (alreadyStopping == null)
            {
                if (!shutdown)
                {
                    string url = thread?.ThreadUri.AbsoluteUri ?? board!.BoardUri.AbsoluteUri;
                    SessionSnapshot next = RemovalSnapshot(url, thread != null);
                    await SaveSessionAsync(next).ConfigureAwait(false);
                    _session = next;
                    _hasMutations = true;
                    if (thread != null) _suppressed.Add(key);
                }
                lock (_state) { watch.Stopping = true; }
                DetachObservers(watch);
            }
        }
        finally { _gate.Release(); }
        if (alreadyStopping != null) { await alreadyStopping.ConfigureAwait(false); return; }
        try { await StopWatchAsync(watch!).ConfigureAwait(false); }
        finally
        {
            watch!.OwnerRegistration?.Dispose();
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_state) { _watches.Remove(key); }
                watch!.Stopped.TrySetResult();
            }
            finally { _gate.Release(); }
            SignalChanged();
        }
    }

    private Task StopWatchAsync(Watch watch)
    {
        if (watch.Thread == null) return watch.Board!.StopMonitoringAsync();
        Task cancelled = _queue.CancelDownloadsForThreadAsync(watch.Thread);
        Task stopped;
        try { stopped = watch.Thread.StopMonitoringAsync(); }
        catch (Exception exception) { stopped = Task.FromException(exception); }
        return Task.WhenAll(cancelled, stopped);
    }

    private SessionSnapshot AdmissionSnapshot(Resource resource, string root)
    {
        SessionSnapshot next = CopySnapshot(_session);
        if (resource.Thread != null)
        {
            next.ThreadUrls = next.ThreadUrls.Append(resource.Url).Distinct(StringComparer.Ordinal).ToArray();
            next.SuppressedThreadUrls = next.SuppressedThreadUrls.Where(url => url != resource.Url).ToArray();
        }
        else next.BoardUrls = next.BoardUrls.Append(resource.Url).Distinct(StringComparer.Ordinal).ToArray();
        next.DownloadDirectories[resource.Url] = root;
        return next;
    }

    private SessionSnapshot RemovalSnapshot(string url, bool thread)
    {
        SessionSnapshot next = CopySnapshot(_session);
        if (thread)
        {
            next.ThreadUrls = next.ThreadUrls.Where(value => value != url).ToArray();
            next.SuppressedThreadUrls = next.SuppressedThreadUrls.Append(url).Distinct(StringComparer.Ordinal).ToArray();
        }
        else next.BoardUrls = next.BoardUrls.Where(value => value != url).ToArray();
        next.DownloadDirectories.Remove(url);
        return next;
    }

    private Task SaveSessionAsync(SessionSnapshot snapshot)
        => _persistSession && _sessionStore != null ? _sessionStore.SaveAsync(CopySnapshot(snapshot)) : Task.CompletedTask;

    private void EnsureRestoredForPersistence()
    {
        if (_persistSession && _restoreRequired && !_restored)
            throw new InvalidOperationException("Call RestoreAsync before adding persistent session watches.");
    }

    private static SessionSnapshot CopySnapshot(SessionSnapshot snapshot) => new SessionSnapshot
    {
        SchemaVersion = snapshot.SchemaVersion,
        ThreadUrls = snapshot.ThreadUrls.ToArray(), BoardUrls = snapshot.BoardUrls.ToArray(),
        SuppressedThreadUrls = snapshot.SuppressedThreadUrls.ToArray(),
        DownloadDirectories = new Dictionary<string, string>(snapshot.DownloadDirectories, StringComparer.Ordinal)
    };

    private HashSet<string> NormalizeRestoredUrls(IEnumerable<string> urls, bool threads,
        Dictionary<ResourceKey, Resource> resources, Dictionary<string, string> canonicalUrls, List<string> warnings)
    {
        HashSet<string> normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (string url in urls)
        {
            try
            {
                AdmissionStatus status = Resolve(url, out Resource? resource);
                if (resource != null && (resource.Thread != null) == threads)
                {
                    normalized.Add(resource.Url);
                    canonicalUrls[url] = resource.Url;
                    resources.TryAdd(resource.Key, resource);
                }
                else { normalized.Add(url); warnings.Add($"Could not restore watch '{url}' ({status}); its saved entry was retained."); }
            }
            catch (Exception exception) { normalized.Add(url); warnings.Add($"Could not resolve watch '{url}': {exception.Message}. Its saved entry was retained."); }
        }
        return normalized;
    }

    private AdmissionStatus Resolve(string url, out Resource? resource)
    {
        resource = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo))
            return AdmissionStatus.Invalid;
        IImageBoard? provider = _providers.FirstOrDefault(p => p.CanHandle(uri));
        if (provider == null) return AdmissionStatus.Unsupported;
        if (provider.IsThreadUri(uri))
        {
            IThread thread = provider.GetThread(uri);
            resource = new Resource(Key(thread), thread.ThreadUri.AbsoluteUri, thread, null);
        }
        else if (provider.IsBoardUri(uri))
        {
            IBoard board = provider.GetBoard(uri);
            resource = new Resource(Key(board), board.BoardUri.AbsoluteUri, null, board);
        }
        else return AdmissionStatus.Invalid;
        return AdmissionStatus.Added;
    }

    private static void DetachObservers(Watch watch)
    {
        if (watch.StateHandler != null)
        {
            if (watch.Thread != null) watch.Thread.PropertyChanged -= watch.StateHandler;
            if (watch.Board != null) watch.Board.PropertyChanged -= watch.StateHandler;
        }
    }

    public Task StopAsync()
    {
        lock (_shutdownLock) { return _shutdown ??= StopCoreAsync(); }
    }

    private async Task StopCoreAsync()
    {
        Watch[] watches;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_state) { _stopping = true; watches = _watches.Values.ToArray(); }
        }
        finally { _gate.Release(); }
        Task queueStopped = _queue.StopAllAsync();
        Task[] removals = watches.Select(w => w.Thread != null
            ? RemoveAsync(Key(w.Thread), w.Thread, null, shutdown: true)
            : RemoveAsync(Key(w.Board!), null, w.Board, shutdown: true)).ToArray();
        await Task.WhenAll(removals.Append(queueStopped)).ConfigureAwait(false);
        Task[] background;
        lock (_state) { background = _backgroundTasks.ToArray(); }
        await Task.WhenAll(background).ConfigureAwait(false);
    }

    private void Track(Func<Task> operation)
    {
        Task task = Task.Run(async () =>
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Error(exception, "Session callback failed"); Warn($"Session change failed: {exception.Message}"); }
        });
        lock (_state) { _backgroundTasks.Add(task); }
        _ = task.ContinueWith(completed => { lock (_state) { _backgroundTasks.Remove(completed); } },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void SignalChanged() => _stateChanges.Signal();

    private void Warn(string warning)
    {
        lock (_state)
        {
            if (_warnings.Count == 32) _warnings.Dequeue();
            _warnings.Enqueue(warning);
        }
        _logger.Warning("{SessionWarning}", warning);
        SignalChanged();
    }
    private static ResourceKey Key(IThread thread) => new ResourceKey(thread.Board.ImageBoard.Name, thread.Board.BoardCode, thread.ThreadId);
    private static ResourceKey Key(IBoard board) => new ResourceKey(board.ImageBoard.Name, board.BoardCode);
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _queue.StateChanged -= SignalChanged;
            _stateChanges.Dispose();
        }
    }
}

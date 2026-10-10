using System.Collections.Concurrent;
using System.Diagnostics;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;

namespace oChan.Tests;

public sealed class SessionPersistenceTests
{
    private const string ThreadUrl = "https://stub.test/g/thread/123";
    private const string OtherThreadUrl = "https://stub.test/g/thread/456";
    private const string SuppressedThreadUrl = "https://stub.test/g/thread/789";
    private const string BoardUrl = "https://stub.test/g/";
    private const long InitialBandwidth = 1024 * 1024;
    private const string CrashProfileVariable = "OCHAN_REGISTRY_CRASH_PROFILE";
    private const string CrashRootVariable = "OCHAN_REGISTRY_CRASH_DOWNLOAD_ROOT";
    private const string CrashMarkerVariable = "OCHAN_REGISTRY_CRASH_MARKER";
    private const string CrashAcknowledgement = "registry-committed:";

    [Fact]
    public async Task ConstructionDoesNotLoadOrStartTheSavedSession()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot { BoardUrls = [BoardUrl], ThreadUrls = [ThreadUrl] });
        await using Registry registry = CreateRegistry(provider, directory.Path, store);

        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0, provider.Board.Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentMutationBeforeExplicitRestoreCannotOverwriteTheExistingSession(bool applyOptions)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        string savedRoot = Path.Combine(directory.Path, "previous-root");
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot
        {
            ThreadUrls = [ThreadUrl],
            DownloadDirectories = new Dictionary<string, string> { [ThreadUrl] = savedRoot }
        });
        ConcurrentBag<RecordingThread> created = new ConcurrentBag<RecordingThread>();
        provider.ThreadFactory = uri =>
        {
            RecordingThread thread = new RecordingThread(provider.Board, uri);
            created.Add(thread);
            return thread;
        };
        DownloadQueue queue = new DownloadQueue(1, InitialBandwidth);
        await using Registry registry = CreateRegistry(provider, directory.Path, store, queue: queue);
        int settingsCommits = 0;

        InvalidOperationException failure = applyOptions
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyOptionsAsync(
                Options(Path.Combine(directory.Path, "uncommitted-root"), 3, InitialBandwidth * 2),
                persistSession: true, commitSettings: () => Interlocked.Increment(ref settingsCommits)))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(OtherThreadUrl));

        Assert.Contains(nameof(Registry.RestoreAsync), failure.Message);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, settingsCommits);
        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
        Assert.Equal(savedRoot, store.Current.DownloadDirectories[ThreadUrl]);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0, created.Sum(thread => thread.Starts));
        Assert.Equal(1, queue.MaxParallelDownloads);
        Assert.Equal(InitialBandwidth, queue.MaxBandwidthBytesPerSecond);

        await registry.RestoreAsync();
        AdmissionResult admitted = await registry.AddAsync(OtherThreadUrl);

        Assert.Equal(AdmissionStatus.Added, admitted.Status);
        Assert.Equal(new[] { ThreadUrl, OtherThreadUrl }.Order(), store.Current.ThreadUrls.Order());
        Assert.Equal(savedRoot, store.Current.DownloadDirectories[ThreadUrl]);
        Assert.Equal(directory.Path, store.Current.DownloadDirectories[OtherThreadUrl]);
        Assert.Equal(2, created.Sum(thread => thread.Starts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionWaitsForDurableSaveBeforeActivationOrNotification(bool boardAdmission)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        RecordingThread thread = new RecordingThread(provider.Board, new Uri(ThreadUrl));
        RecordingBoard board = new RecordingBoard(provider);
        provider.ThreadFactory = _ => thread;
        provider.BoardFactory = _ => board;
        ScriptedSessionStore store = new ScriptedSessionStore();
        TaskCompletionSource<SessionSnapshot> saving = NewCompletion<SessionSnapshot>();
        TaskCompletionSource commit = NewCompletion();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        store.BeforeSaveAsync = snapshot => { saving.TrySetResult(snapshot); return commit.Task; };
        TaskCompletionSource<bool> observedAdmissionWasDurable = NewCompletion<bool>();
        registry.StateChanged += () =>
        {
            bool visible = boardAdmission ? registry.Boards.Contains(board) : registry.Threads.Contains(thread);
            if (!visible) return;
            SessionSnapshot durable = store.Current;
            observedAdmissionWasDurable.TrySetResult((boardAdmission ? durable.BoardUrls : durable.ThreadUrls)
                .Contains(boardAdmission ? BoardUrl : ThreadUrl));
        };

        Task<AdmissionResult> admission = registry.AddAsync(boardAdmission ? BoardUrl : ThreadUrl);
        try
        {
            SessionSnapshot proposed = await saving.Task.WaitAsync(TestWait.Timeout);
            Assert.False(admission.IsCompleted);
            Assert.Empty(registry.Threads);
            Assert.Empty(registry.Boards);
            Assert.Equal(0, thread.Starts);
            Assert.Equal(0, board.Starts);
            Assert.False(observedAdmissionWasDurable.Task.IsCompleted);
            Assert.Equal(directory.Path, proposed.DownloadDirectories[boardAdmission ? BoardUrl : ThreadUrl]);

            commit.TrySetResult();
            Assert.Equal(AdmissionStatus.Added, (await admission.WaitAsync(TestWait.Timeout)).Status);
            Assert.Equal(1, boardAdmission ? board.Starts : thread.Starts);
            Assert.Equal(1, boardAdmission ? registry.Boards.Count : registry.Threads.Count);
            Assert.True(await observedAdmissionWasDurable.Task.WaitAsync(TestWait.Timeout));
            Assert.Single(boardAdmission ? store.Current.BoardUrls : store.Current.ThreadUrls);
        }
        finally
        {
            commit.TrySetResult();
            await admission.WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAdmissionSaveDoesNotActivateAndAllowsAnExplicitRetry(bool boardAdmission)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        RecordingThread thread = new RecordingThread(provider.Board, new Uri(ThreadUrl));
        RecordingBoard board = new RecordingBoard(provider);
        provider.ThreadFactory = _ => thread;
        provider.BoardFactory = _ => board;
        ScriptedSessionStore store = new ScriptedSessionStore();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        store.SaveFailure = new IOException("Synthetic disk-full failure");

        await Assert.ThrowsAsync<IOException>(() => registry.AddAsync(boardAdmission ? BoardUrl : ThreadUrl));

        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Empty(store.Current.ThreadUrls);
        Assert.Empty(store.Current.BoardUrls);
        Assert.Equal(0, thread.Starts);
        Assert.Equal(0, board.Starts);

        store.SaveFailure = null;
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(boardAdmission ? BoardUrl : ThreadUrl)).Status);
        Assert.Equal(1, boardAdmission ? board.Starts : thread.Starts);
        Assert.Equal(1, boardAdmission ? registry.Boards.Count : registry.Threads.Count);
        Assert.Single(boardAdmission ? store.Current.BoardUrls : store.Current.ThreadUrls);
    }

    [Fact]
    public async Task SetupFailureCompensatesTheDurableIntentAndStopsPartialMonitoring()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        SetupFailureThread failed = new SetupFailureThread(provider.Board, new Uri(ThreadUrl));
        RecordingThread successful = new RecordingThread(provider.Board, new Uri(ThreadUrl));
        int attempts = 0;
        provider.ThreadFactory = uri => uri.AbsolutePath.EndsWith("/123", StringComparison.Ordinal)
            ? (Interlocked.Increment(ref attempts) == 1 ? failed : successful)
            : new RecordingThread(provider.Board, uri);
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        await registry.AddAsync(OtherThreadUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(ThreadUrl));

        Assert.Single(registry.Threads, thread => thread.ThreadId == "456");
        Assert.Equal(new[] { OtherThreadUrl }, store.Current.ThreadUrls);
        Assert.DoesNotContain(ThreadUrl, store.Current.DownloadDirectories.Keys);
        Assert.Equal(1, failed.Stops);
        int checksAfterCleanup = failed.Checks;
        failed.Clock.Advance();

        AdmissionResult retry = await registry.AddAsync(ThreadUrl);
        Assert.Equal(AdmissionStatus.Added, retry.Status);
        await TestWait.UntilAsync(() => successful.Clock.Waits == 1);
        Assert.Equal(checksAfterCleanup, failed.Checks);
        Assert.Equal(1, successful.Starts);
        Assert.Equal(2, registry.Threads.Count);
        Assert.Contains(successful, registry.Threads);
        Assert.Equal(new[] { ThreadUrl, OtherThreadUrl }.Order(), store.Current.ThreadUrls.Order());
    }

    [Fact]
    public async Task FailedSetupCompensationReportsBothErrorsWithoutLeavingAnActiveWatch()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        SetupFailureThread failed = new SetupFailureThread(provider.Board, new Uri(ThreadUrl));
        provider.ThreadFactory = _ => failed;
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        int previousSaves = store.SaveCalls;
        store.BeforeSaveAsync = _ => store.SaveCalls == previousSaves + 2
            ? Task.FromException(new IOException("Synthetic compensation write failure"))
            : Task.CompletedTask;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => registry.AddAsync(ThreadUrl));

        Assert.Contains(failure.Flatten().InnerExceptions, error => error is InvalidOperationException);
        Assert.Contains(failure.Flatten().InnerExceptions, error => error is IOException);
        Assert.Empty(registry.Threads);
        Assert.Equal(1, failed.Stops);
        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
    }

    [Fact]
    public async Task AcknowledgedAddsAreImmediatelyReadableThroughANewSessionStore()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        SessionStore store = new SessionStore(directory.Path, lease);
        string root = Path.Combine(directory.Path, "downloads");
        await using Registry registry = CreateRegistry(provider, root, store);
        await registry.RestoreAsync();

        await registry.AddAsync("https://stub.test/g/catalog");
        await registry.AddAsync(ThreadUrl + "/a-title#p234");
        SessionLoadResult durable = await new SessionStore(directory.Path, lease).LoadAsync();

        Assert.Equal(new[] { BoardUrl }, durable.Snapshot.BoardUrls);
        Assert.Equal(new[] { ThreadUrl }, durable.Snapshot.ThreadUrls);
        Assert.Equal(root, durable.Snapshot.DownloadDirectories[BoardUrl]);
        Assert.Equal(root, durable.Snapshot.DownloadDirectories[ThreadUrl]);
        Assert.False(durable.Recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalWaitsForSaveWhileTheCurrentWatchKeepsRunning(bool boardRemoval)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        RecordingThread thread = new RecordingThread(provider.Board, new Uri(ThreadUrl));
        RecordingBoard board = new RecordingBoard(provider);
        provider.ThreadFactory = _ => thread;
        provider.BoardFactory = _ => board;
        ScriptedSessionStore store = new ScriptedSessionStore();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        await registry.AddAsync(boardRemoval ? BoardUrl : ThreadUrl);
        await TestWait.UntilAsync(() => boardRemoval ? board.Clock.Waits == 1 : thread.Clock.Waits == 1);
        TaskCompletionSource<bool> observedRemovalWasDurable = NewCompletion<bool>();
        registry.StateChanged += () =>
        {
            bool removed = boardRemoval ? !registry.Boards.Contains(board) : !registry.Threads.Contains(thread);
            if (!removed) return;
            SessionSnapshot durable = store.Current;
            observedRemovalWasDurable.TrySetResult(!(boardRemoval ? durable.BoardUrls : durable.ThreadUrls)
                .Contains(boardRemoval ? BoardUrl : ThreadUrl));
        };
        TaskCompletionSource saving = NewCompletion();
        TaskCompletionSource commit = NewCompletion();
        store.BeforeSaveAsync = _ => { saving.TrySetResult(); return commit.Task; };

        Task removal = boardRemoval ? registry.RemoveBoardAsync(board) : registry.RemoveThreadAsync(thread);
        try
        {
            await saving.Task.WaitAsync(TestWait.Timeout);
            Assert.False(removal.IsCompleted);
            Assert.Equal(1, boardRemoval ? registry.Boards.Count : registry.Threads.Count);
            Assert.Equal(0, board.Stops);
            Assert.Equal(0, thread.Stops);
            Assert.False(observedRemovalWasDurable.Task.IsCompleted);
            Assert.Single(boardRemoval ? store.Current.BoardUrls : store.Current.ThreadUrls);
            if (boardRemoval) board.Clock.Advance();
            else thread.Clock.Advance();
            await TestWait.UntilAsync(() => boardRemoval ? board.Clock.Waits == 2 : thread.Clock.Waits == 2);

            commit.TrySetResult();
            await removal.WaitAsync(TestWait.Timeout);
            Assert.Empty(registry.Threads);
            Assert.Empty(registry.Boards);
            Assert.Equal(1, boardRemoval ? board.Stops : thread.Stops);
            Assert.True(await observedRemovalWasDurable.Task.WaitAsync(TestWait.Timeout));
            Assert.Empty(boardRemoval ? store.Current.BoardUrls : store.Current.ThreadUrls);
        }
        finally
        {
            commit.TrySetResult();
            await removal.WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRemovalSavePreservesTheWatchAndAllowsAnExplicitRetry(bool boardRemoval)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        RecordingThread thread = new RecordingThread(provider.Board, new Uri(ThreadUrl));
        RecordingBoard board = new RecordingBoard(provider);
        provider.ThreadFactory = _ => thread;
        provider.BoardFactory = _ => board;
        ScriptedSessionStore store = new ScriptedSessionStore();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        await registry.AddAsync(boardRemoval ? BoardUrl : ThreadUrl);
        await TestWait.UntilAsync(() => boardRemoval ? board.Clock.Waits == 1 : thread.Clock.Waits == 1);
        store.SaveFailure = new IOException("Synthetic read-only profile");

        await Assert.ThrowsAsync<IOException>(() => boardRemoval ? registry.RemoveBoardAsync(board) : registry.RemoveThreadAsync(thread));

        Assert.Equal(1, boardRemoval ? registry.Boards.Count : registry.Threads.Count);
        Assert.Single(boardRemoval ? store.Current.BoardUrls : store.Current.ThreadUrls);
        Assert.Empty(store.Current.SuppressedThreadUrls);
        Assert.Equal(0, thread.Stops);
        Assert.Equal(0, board.Stops);
        if (boardRemoval) board.Clock.Advance();
        else thread.Clock.Advance();
        await TestWait.UntilAsync(() => boardRemoval ? board.Clock.Waits == 2 : thread.Clock.Waits == 2);

        store.SaveFailure = null;
        if (boardRemoval) await registry.RemoveBoardAsync(board);
        else await registry.RemoveThreadAsync(thread);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Empty(boardRemoval ? store.Current.BoardUrls : store.Current.ThreadUrls);
    }

    [Fact]
    public async Task AcknowledgedRemovalPersistsSuppressionAndExplicitReaddClearsIt()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        SessionStore store = new SessionStore(directory.Path, lease);
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        IThread thread = (await registry.AddAsync(ThreadUrl)).Thread!;

        await registry.RemoveThreadAsync(thread);
        SessionSnapshot removed = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;
        Assert.Empty(removed.ThreadUrls);
        Assert.Equal(new[] { ThreadUrl }, removed.SuppressedThreadUrls);

        AdmissionResult readmission = await registry.AddAsync(ThreadUrl + "/a-title#p987");
        SessionSnapshot readmitted = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;
        Assert.Equal(AdmissionStatus.Added, readmission.Status);
        Assert.NotSame(thread, readmission.Thread);
        Assert.Equal(new[] { ThreadUrl }, readmitted.ThreadUrls);
        Assert.Empty(readmitted.SuppressedThreadUrls);
    }

    [Fact]
    public async Task FailedExplicitReadmissionPreservesSuppressionForLaterMutations()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot { SuppressedThreadUrls = [ThreadUrl] });
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        store.SaveFailure = new IOException("Synthetic readmission write failure");

        await Assert.ThrowsAsync<IOException>(() => registry.AddAsync(ThreadUrl));
        store.SaveFailure = null;
        await registry.AddAsync(OtherThreadUrl);

        Assert.Equal(new[] { ThreadUrl }, store.Current.SuppressedThreadUrls);
        Assert.Equal(new[] { OtherThreadUrl }, store.Current.ThreadUrls);
        Assert.DoesNotContain(registry.Threads, thread => thread.ThreadId == "123");
    }

    [Fact]
    public async Task RestartedBoardDoesNotReactivateAManuallyRemovedThread()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard firstProvider = new StubImageBoard();
        await using (Registry first = CreateRegistry(firstProvider, directory.Path, new SessionStore(directory.Path, lease)))
        {
            await first.RestoreAsync();
            await first.AddAsync(BoardUrl);
            IThread removed = (await first.AddAsync(ThreadUrl)).Thread!;
            await first.RemoveThreadAsync(removed);
        }
        using StubImageBoard restoredProvider = new StubImageBoard();
        ConcurrentBag<RecordingThread> created = new ConcurrentBag<RecordingThread>();
        restoredProvider.ThreadFactory = uri =>
        {
            RecordingThread thread = new RecordingThread(restoredProvider.Board, uri);
            created.Add(thread);
            return thread;
        };
        restoredProvider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>(
            [restoredProvider.GetThread(new Uri(ThreadUrl)), restoredProvider.GetThread(new Uri(OtherThreadUrl))]);
        await using Registry restored = CreateRegistry(restoredProvider, directory.Path, new SessionStore(directory.Path, lease));

        await restored.RestoreAsync();
        await TestWait.UntilAsync(() => restored.Threads.Any(thread => thread.ThreadId == "456"));
        await restored.StopAsync().WaitAsync(TestWait.Timeout);
        SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;

        Assert.Equal(0, created.Where(thread => thread.ThreadId == "123").Sum(thread => thread.Starts));
        Assert.Equal(new[] { ThreadUrl }, durable.SuppressedThreadUrls);
        Assert.DoesNotContain(ThreadUrl, durable.ThreadUrls);
        Assert.Contains(OtherThreadUrl, durable.ThreadUrls);
    }

    [Fact]
    public async Task ShutdownLeavesTheDurableWatchlistAndSuppressionUnchanged()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        SessionStore store = new SessionStore(directory.Path, lease);
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        await registry.AddAsync(BoardUrl);
        await registry.AddAsync(ThreadUrl);
        IThread removed = (await registry.AddAsync(SuppressedThreadUrl)).Thread!;
        await registry.RemoveThreadAsync(removed);
        string sessionPath = Path.Combine(directory.Path, "saved_urls.json");
        string durableBeforeStop = File.ReadAllText(sessionPath);

        await registry.StopAsync().WaitAsync(TestWait.Timeout);
        await registry.StopAsync().WaitAsync(TestWait.Timeout);
        SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;

        Assert.Equal(durableBeforeStop, File.ReadAllText(sessionPath));
        Assert.Equal(new[] { BoardUrl }, durable.BoardUrls);
        Assert.Equal(new[] { ThreadUrl }, durable.ThreadUrls);
        Assert.Equal(new[] { SuppressedThreadUrl }, durable.SuppressedThreadUrls);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
    }

    [Fact]
    public async Task ShutdownDoesNotConsultAnUnavailableSessionStore()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        await registry.AddAsync(BoardUrl);
        await registry.AddAsync(ThreadUrl);
        int saved = store.SaveCalls;
        int loaded = store.LoadCalls;
        store.SaveFailure = new IOException("Profile disk is no longer available");
        store.LoadFailure = new IOException("Profile disk is no longer available");

        await registry.StopAsync().WaitAsync(TestWait.Timeout);
        await registry.DisposeAsync();

        Assert.Equal(saved, store.SaveCalls);
        Assert.Equal(loaded, store.LoadCalls);
        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
        Assert.Equal(new[] { BoardUrl }, store.Current.BoardUrls);
    }

    [Fact]
    public async Task PublicRemovalDuringShutdownCannotAcknowledgeADurableDeletion()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        SessionStore store = new SessionStore(directory.Path, lease);
        TaskCompletionSource checking = NewCompletion();
        TaskCompletionSource releaseCheck = NewCompletion();
        ProbeThread thread = new ProbeThread(provider.Board, new Uri(ThreadUrl))
        {
            OnCheck = () => { checking.TrySetResult(); return releaseCheck.Task; }
        };
        provider.ThreadFactory = _ => thread;
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        Task? shutdown = null;
        try
        {
            await registry.AddAsync(ThreadUrl);
            await checking.Task.WaitAsync(TestWait.Timeout);
            string sessionPath = Path.Combine(directory.Path, "saved_urls.json");
            string committed = File.ReadAllText(sessionPath);

            shutdown = registry.StopAsync();
            await TestWait.UntilAsync(() => registry.Threads.Count == 0);
            Assert.False(shutdown.IsCompleted);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.RemoveThreadAsync(thread).WaitAsync(TestWait.Timeout));

            Assert.False(shutdown.IsCompleted);
            Assert.Equal(committed, File.ReadAllText(sessionPath));
            SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;
            Assert.Equal(new[] { ThreadUrl }, durable.ThreadUrls);
            Assert.Empty(durable.SuppressedThreadUrls);
            Assert.Equal(directory.Path, durable.DownloadDirectories[ThreadUrl]);
        }
        finally
        {
            releaseCheck.TrySetResult();
            if (shutdown != null) await shutdown.WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task AcknowledgedRegistryMutationsSurviveAbruptProcessTermination()
    {
        using TempDirectory directory = new TempDirectory();
        string profile = Path.Combine(directory.Path, "child-profile");
        string root = Path.Combine(directory.Path, "child-downloads");
        string marker = Path.Combine(directory.Path, "registry-ack.txt");
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(SessionPersistenceTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=oChan.Tests.SessionPersistenceTests.RegistryAbruptExitChildProbe");
        start.Environment[CrashProfileVariable] = profile;
        start.Environment[CrashRootVariable] = root;
        start.Environment[CrashMarkerVariable] = marker;

        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Registry crash subprocess.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        Process? testHost = null;
        Exception? probeFailure = null;
        try
        {
            int testHostId = await WaitForCrashAcknowledgementAsync(child, marker);
            testHost = Process.GetProcessById(testHostId);
            Assert.False(child.HasExited, "The test runner exited before the deliberate process kill.");
            Assert.False(testHost.HasExited, "The Registry testhost exited before the deliberate process kill.");
        }
        catch (Exception exception)
        {
            probeFailure = exception;
        }
        finally
        {
            try
            {
                // Kill the runner's entire tree, including the testhost that owns the Registry.
                await KillProbeProcessAsync(child);
            }
            finally
            {
                if (testHost != null)
                {
                    using (testHost) { await KillProbeProcessAsync(testHost); }
                }
            }
        }

        string capturedOutput = await output.WaitAsync(TestWait.Timeout);
        string capturedErrors = await errors.WaitAsync(TestWait.Timeout);
        Assert.True(probeFailure == null, $"Registry crash subprocess failed: {probeFailure}\n{capturedOutput}\n{capturedErrors}");
        Assert.NotEqual(0, child.ExitCode);

        // Read the committed primary before any Registry restore can normalize it.
        using ProfileLease lease = ProfileLease.Acquire(profile);
        SessionLoadResult durable = await new SessionStore(profile, lease).LoadAsync();
        Assert.False(durable.Recovered, durable.Warning);
        Assert.Null(durable.Warning);
        Assert.Equal(new[] { BoardUrl }, durable.Snapshot.BoardUrls);
        Assert.Equal(new[] { ThreadUrl }, durable.Snapshot.ThreadUrls);
        Assert.Equal(new[] { SuppressedThreadUrl }, durable.Snapshot.SuppressedThreadUrls);
        Assert.Equal(root, durable.Snapshot.DownloadDirectories[BoardUrl]);
        Assert.Equal(root, durable.Snapshot.DownloadDirectories[ThreadUrl]);
    }

    [Fact]
    public async Task RegistryAbruptExitChildProbe()
    {
        string? profile = Environment.GetEnvironmentVariable(CrashProfileVariable);
        if (profile == null) return;
        string root = Environment.GetEnvironmentVariable(CrashRootVariable)
            ?? throw new InvalidOperationException("Crash probe download root was not supplied.");
        string marker = Environment.GetEnvironmentVariable(CrashMarkerVariable)
            ?? throw new InvalidOperationException("Crash probe acknowledgement path was not supplied.");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        using StubImageBoard provider = new StubImageBoard();
        await using Registry registry = CreateRegistry(provider, root, new SessionStore(profile, lease));
        await registry.RestoreAsync();
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(BoardUrl)).Status);
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(ThreadUrl)).Status);
        AdmissionResult removable = await registry.AddAsync(SuppressedThreadUrl);
        Assert.Equal(AdmissionStatus.Added, removable.Status);
        await registry.RemoveThreadAsync(removable.Thread!);
        Assert.DoesNotContain(registry.Threads, thread => thread.ThreadId == "789");

        await File.WriteAllTextAsync(marker, CrashAcknowledgement + Environment.ProcessId);
        // The parent kills this process while the Registry and lease are still alive.
        // No normal StopAsync/DisposeAsync or test completion can precede that kill.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task RestoreDedupeAndSuppressionApplyBeforeTheFirstCatalogDiscovery()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        string savedRoot = Path.Combine(directory.Path, "saved");
        string defaultRoot = Path.Combine(directory.Path, "current-default");
        ConcurrentBag<RecordingThread> created = new ConcurrentBag<RecordingThread>();
        provider.ThreadFactory = uri =>
        {
            RecordingThread thread = new RecordingThread(provider.Board, uri);
            created.Add(thread);
            return thread;
        };
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>(
            [provider.GetThread(new Uri(ThreadUrl)), provider.GetThread(new Uri(SuppressedThreadUrl)), provider.GetThread(new Uri(OtherThreadUrl))]);
        SessionSnapshot initial = new SessionSnapshot
        {
            BoardUrls = [BoardUrl, "https://stub.test/g/catalog"],
            ThreadUrls = [ThreadUrl, ThreadUrl + "/a-title#p1"],
            SuppressedThreadUrls = [SuppressedThreadUrl + "/another-title#p9"],
            DownloadDirectories = new Dictionary<string, string>
            {
                [BoardUrl] = savedRoot,
                [ThreadUrl] = savedRoot
            }
        };
        ScriptedSessionStore store = new ScriptedSessionStore(initial);
        await using Registry registry = CreateRegistry(provider, defaultRoot, store);

        await registry.RestoreAsync();
        await TestWait.UntilAsync(() => registry.Threads.Count == 2);
        await registry.RestoreAsync();

        Assert.Single(registry.Boards);
        Assert.Equal(1, store.LoadCalls);
        Assert.DoesNotContain(registry.Threads, thread => thread.ThreadId == "789");
        Assert.All(registry.Threads, thread => Assert.Equal(Path.Combine(savedRoot, "stub", "g", thread.ThreadId), thread.DownloadDirectory));
        Assert.Equal(Path.Combine(savedRoot, "stub", "g"), registry.GetDownloadDirectory(provider.Board));
        Assert.Equal(1, created.Where(thread => thread.ThreadId == "123").Sum(thread => thread.Starts));
        Assert.Equal(0, created.Where(thread => thread.ThreadId == "789").Sum(thread => thread.Starts));
        Assert.All(store.SavedSnapshots, snapshot =>
        {
            Assert.Contains(BoardUrl, snapshot.BoardUrls);
            Assert.Contains(ThreadUrl, snapshot.ThreadUrls);
            Assert.Contains(snapshot.SuppressedThreadUrls, url => url.StartsWith(SuppressedThreadUrl, StringComparison.Ordinal));
        });
        Assert.Equal(savedRoot, store.Current.DownloadDirectories[OtherThreadUrl]);
    }

    [Fact]
    public async Task RestoreAndFollowingMutationDoNotDiscardResourcesForUnavailableProviders()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        const string unavailable = "https://future-provider.test/b/thread/222";
        string savedRoot = Path.Combine(directory.Path, "archived");
        SessionSnapshot initial = new SessionSnapshot
        {
            BoardUrls = [BoardUrl],
            ThreadUrls = [ThreadUrl, unavailable],
            DownloadDirectories = new Dictionary<string, string> { [unavailable] = savedRoot }
        };
        ScriptedSessionStore store = new ScriptedSessionStore(initial);
        await using Registry registry = CreateRegistry(provider, directory.Path, store);

        await registry.RestoreAsync();
        await registry.AddAsync(OtherThreadUrl);

        Assert.NotEmpty(registry.GetSnapshot().Warnings);
        Assert.All(store.SavedSnapshots, snapshot =>
        {
            Assert.Contains(ThreadUrl, snapshot.ThreadUrls);
            Assert.Contains(unavailable, snapshot.ThreadUrls);
            Assert.Contains(BoardUrl, snapshot.BoardUrls);
            Assert.Equal(savedRoot, snapshot.DownloadDirectories[unavailable]);
        });
    }

    [Fact]
    public async Task RestorePublishesRecoveryWarningAndDoesNotSaveLoadedStateIncrementally()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot { ThreadUrls = [ThreadUrl, OtherThreadUrl], BoardUrls = [BoardUrl] })
        {
            LoadWarning = "Recovered the session from a backup.",
            Recovered = true
        };
        await using Registry registry = CreateRegistry(provider, directory.Path, store);

        await registry.RestoreAsync();

        Assert.Contains(store.LoadWarning, registry.GetSnapshot().Warnings);
        Assert.Equal(2, registry.Threads.Count);
        Assert.Single(registry.Boards);
        Assert.All(store.SavedSnapshots, snapshot =>
        {
            Assert.Contains(ThreadUrl, snapshot.ThreadUrls);
            Assert.Contains(OtherThreadUrl, snapshot.ThreadUrls);
            Assert.Contains(BoardUrl, snapshot.BoardUrls);
        });
    }

    [Fact]
    public async Task LegacyRootMigrationCommitsTheCompleteSnapshotBeforeAnyWatchStarts()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        const string unavailable = "https://future-provider.test/b/thread/222";
        string root = Path.Combine(directory.Path, "assigned-root");
        RecordingBoard board = new RecordingBoard(provider);
        ConcurrentBag<RecordingThread> threads = new ConcurrentBag<RecordingThread>();
        provider.BoardFactory = _ => board;
        provider.ThreadFactory = uri =>
        {
            RecordingThread thread = new RecordingThread(board, uri);
            threads.Add(thread);
            return thread;
        };
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot
        {
            ThreadUrls = [ThreadUrl + "/a-title#p456", unavailable],
            BoardUrls = ["https://stub.test/g/catalog"],
            SuppressedThreadUrls = [SuppressedThreadUrl + "/removed-title#p1"]
        });
        TaskCompletionSource<SessionSnapshot> saving = NewCompletion<SessionSnapshot>();
        TaskCompletionSource commit = NewCompletion();
        store.BeforeSaveAsync = snapshot => { saving.TrySetResult(snapshot); return commit.Task; };
        await using Registry registry = CreateRegistry(provider, root, store);
        Task restore = registry.RestoreAsync();
        try
        {
            SessionSnapshot proposed = await saving.Task.WaitAsync(TestWait.Timeout);
            Assert.False(restore.IsCompleted);
            Assert.Empty(registry.Threads);
            Assert.Empty(registry.Boards);
            Assert.Equal(0, board.Starts);
            Assert.Equal(0, threads.Sum(thread => thread.Starts));
            Assert.Equal(new[] { ThreadUrl, unavailable }.Order(), proposed.ThreadUrls.Order());
            Assert.Equal(new[] { BoardUrl }, proposed.BoardUrls);
            Assert.Equal(new[] { SuppressedThreadUrl }, proposed.SuppressedThreadUrls);
            Assert.Equal(root, proposed.DownloadDirectories[ThreadUrl]);
            Assert.Equal(root, proposed.DownloadDirectories[BoardUrl]);

            commit.TrySetResult();
            await restore.WaitAsync(TestWait.Timeout);

            Assert.Equal(1, store.SaveCalls);
            Assert.Single(store.SavedSnapshots);
            Assert.Contains(unavailable, store.Current.ThreadUrls);
            Assert.Equal(new[] { SuppressedThreadUrl }, store.Current.SuppressedThreadUrls);
            Assert.Equal(1, board.Starts);
            Assert.Equal(1, threads.Where(thread => thread.ThreadId == "123").Sum(thread => thread.Starts));
            Assert.Equal(0, threads.Where(thread => thread.ThreadId == "789").Sum(thread => thread.Starts));
            Assert.Single(registry.Threads);
            Assert.Single(registry.Boards);
        }
        finally
        {
            commit.TrySetResult();
            await restore.WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task FailedLegacyRootMigrationStartsNothingAndAllowsCompleteRestoreRetry()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        RecordingBoard board = new RecordingBoard(provider);
        ConcurrentBag<RecordingThread> threads = new ConcurrentBag<RecordingThread>();
        provider.BoardFactory = _ => board;
        provider.ThreadFactory = uri =>
        {
            RecordingThread thread = new RecordingThread(board, uri);
            threads.Add(thread);
            return thread;
        };
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot
        {
            ThreadUrls = [ThreadUrl], BoardUrls = [BoardUrl], SuppressedThreadUrls = [SuppressedThreadUrl]
        })
        {
            SaveFailure = new IOException("Synthetic legacy-root migration write failure")
        };
        await using Registry registry = CreateRegistry(provider, directory.Path, store);

        await Assert.ThrowsAsync<IOException>(() => registry.RestoreAsync());

        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0, board.Starts);
        Assert.Equal(0, threads.Sum(thread => thread.Starts));
        Assert.Empty(store.Current.DownloadDirectories);
        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
        Assert.Equal(new[] { SuppressedThreadUrl }, store.Current.SuppressedThreadUrls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(OtherThreadUrl));
        Assert.Equal(1, store.SaveCalls);

        store.SaveFailure = null;
        await registry.RestoreAsync();

        Assert.Equal(2, store.LoadCalls);
        Assert.Equal(2, store.SaveCalls);
        Assert.Single(store.SavedSnapshots);
        Assert.Single(registry.Threads);
        Assert.Single(registry.Boards);
        Assert.Equal(directory.Path, store.Current.DownloadDirectories[ThreadUrl]);
        Assert.Equal(directory.Path, store.Current.DownloadDirectories[BoardUrl]);
        Assert.Equal(1, threads.Where(thread => thread.ThreadId == "123").Sum(thread => thread.Starts));
        Assert.Equal(1, board.Starts);
    }

    [Fact]
    public async Task MigratedLegacyRootsSurviveAnotherRestartWithADifferentDefaultRootWithoutAnotherWrite()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string firstRoot = Path.Combine(directory.Path, "first-default");
        string nextRoot = Path.Combine(directory.Path, "next-default");
        string sessionPath = Path.Combine(directory.Path, "saved_urls.json");
        string legacy = $"{{\"ThreadUrls\":[\"{ThreadUrl}\"],\"BoardUrls\":[\"{BoardUrl}\"]}}";
        File.WriteAllText(sessionPath, legacy);
        CountingSessionStore firstStore = new CountingSessionStore(new SessionStore(directory.Path, lease));
        using (StubImageBoard provider = new StubImageBoard())
        {
            await using Registry first = CreateRegistry(provider, firstRoot, firstStore);
            await first.RestoreAsync();
            Assert.Equal(1, firstStore.SaveCalls);
            Assert.Equal(Path.Combine(firstRoot, "stub", "g", "123"), Assert.Single(first.Threads).DownloadDirectory);
        }
        string migrated = File.ReadAllText(sessionPath);
        Assert.Equal(legacy, File.ReadAllText(sessionPath + ".bak"));
        CountingSessionStore nextStore = new CountingSessionStore(new SessionStore(directory.Path, lease));
        using (StubImageBoard provider = new StubImageBoard())
        {
            await using Registry next = CreateRegistry(provider, nextRoot, nextStore);
            await next.RestoreAsync();
            Assert.Equal(Path.Combine(firstRoot, "stub", "g", "123"), Assert.Single(next.Threads).DownloadDirectory);
            Assert.Equal(Path.Combine(firstRoot, "stub", "g"), next.GetDownloadDirectory(Assert.Single(next.Boards)));
        }

        Assert.Equal(0, nextStore.SaveCalls);
        Assert.Equal(migrated, File.ReadAllText(sessionPath));
        Assert.Equal(legacy, File.ReadAllText(sessionPath + ".bak"));
        SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;
        Assert.Equal(firstRoot, durable.DownloadDirectories[ThreadUrl]);
        Assert.Equal(firstRoot, durable.DownloadDirectories[BoardUrl]);
    }

    [Fact]
    public async Task RecoveredLegacySessionUsesRootsInMemoryWithoutAutomaticallyRewritingEvidence()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        string root = Path.Combine(directory.Path, "recovery-default");
        string sessionPath = Path.Combine(directory.Path, "saved_urls.json");
        const string damaged = "{interrupted-write";
        string backup = $"{{\"ThreadUrls\":[\"{ThreadUrl}\"],\"BoardUrls\":[\"{BoardUrl}\"]}}";
        File.WriteAllText(sessionPath, damaged);
        File.WriteAllText(sessionPath + ".bak", backup);
        CountingSessionStore store = new CountingSessionStore(new SessionStore(directory.Path, lease));
        await using Registry registry = CreateRegistry(provider, root, store);

        await registry.RestoreAsync();

        Assert.Equal(0, store.SaveCalls);
        Assert.NotEmpty(registry.GetSnapshot().Warnings);
        Assert.Equal(Path.Combine(root, "stub", "g", "123"), Assert.Single(registry.Threads).DownloadDirectory);
        Assert.Equal(Path.Combine(root, "stub", "g"), registry.GetDownloadDirectory(Assert.Single(registry.Boards)));
        await registry.StopAsync();
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(damaged, File.ReadAllText(sessionPath));
        Assert.Equal(backup, File.ReadAllText(sessionPath + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "saved_urls.json.corrupt-*"));
    }

    [Fact]
    public async Task LoadFailureDoesNotActivatePartialSessionAndAllowsRestoreRetry()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot { ThreadUrls = [ThreadUrl], BoardUrls = [BoardUrl] })
        {
            LoadFailure = new IOException("Synthetic unavailable profile")
        };
        await using Registry registry = CreateRegistry(provider, directory.Path, store);

        await Assert.ThrowsAsync<IOException>(() => registry.RestoreAsync());
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0, provider.Board.Checks);
        Assert.Equal(0, store.SaveCalls);

        store.LoadFailure = null;
        await registry.RestoreAsync();
        Assert.Single(registry.Threads);
        Assert.Single(registry.Boards);
    }

    [Fact]
    public async Task AdmissionDuringDelayedRestoreWaitsAndPreservesTheCompleteLoadedSnapshot()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        string savedRoot = Path.Combine(directory.Path, "restored-root");
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot
        {
            BoardUrls = [BoardUrl],
            ThreadUrls = [ThreadUrl],
            SuppressedThreadUrls = [SuppressedThreadUrl],
            DownloadDirectories = new Dictionary<string, string> { [ThreadUrl] = savedRoot, [BoardUrl] = savedRoot }
        });
        TaskCompletionSource loading = NewCompletion();
        TaskCompletionSource releaseLoad = NewCompletion();
        store.BeforeLoadAsync = () => { loading.TrySetResult(); return releaseLoad.Task; };
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        Task restore = registry.RestoreAsync();
        Task<AdmissionResult>? addition = null;
        try
        {
            await loading.Task.WaitAsync(TestWait.Timeout);
            addition = registry.AddAsync(OtherThreadUrl);
            Assert.False(restore.IsCompleted);
            Assert.False(addition.IsCompleted);
            Assert.Equal(0, store.SaveCalls);
            Assert.Empty(registry.Threads);
            Assert.Empty(registry.Boards);

            releaseLoad.TrySetResult();
            await restore.WaitAsync(TestWait.Timeout);
            Assert.Equal(AdmissionStatus.Added, (await addition.WaitAsync(TestWait.Timeout)).Status);

            Assert.Equal(new[] { ThreadUrl, OtherThreadUrl }.Order(), store.Current.ThreadUrls.Order());
            Assert.Equal(new[] { BoardUrl }, store.Current.BoardUrls);
            Assert.Equal(new[] { SuppressedThreadUrl }, store.Current.SuppressedThreadUrls);
            Assert.Equal(savedRoot, store.Current.DownloadDirectories[ThreadUrl]);
            Assert.Equal(savedRoot, store.Current.DownloadDirectories[BoardUrl]);
            Assert.Equal(directory.Path, store.Current.DownloadDirectories[OtherThreadUrl]);
            Assert.All(store.SavedSnapshots, snapshot =>
            {
                Assert.Contains(ThreadUrl, snapshot.ThreadUrls);
                Assert.Contains(BoardUrl, snapshot.BoardUrls);
                Assert.Contains(SuppressedThreadUrl, snapshot.SuppressedThreadUrls);
            });
        }
        finally
        {
            releaseLoad.TrySetResult();
            await restore.WaitAsync(TestWait.Timeout);
            if (addition != null) await addition.WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task FailedDiscoverySaveWarnsWithoutActivatingAndTheNextCatalogPollRetries()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        ConcurrentBag<RecordingThread> discovered = new ConcurrentBag<RecordingThread>();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        await registry.AddAsync(BoardUrl);
        await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 1);
        provider.Board.OnCheck = () =>
        {
            RecordingThread thread = new RecordingThread(provider.Board, new Uri(ThreadUrl));
            discovered.Add(thread);
            return Task.FromResult<IEnumerable<IThread>>([thread]);
        };
        store.SaveFailure = new IOException("Synthetic catalog discovery write failure");
        provider.Board.Clock.Advance();
        await TestWait.UntilAsync(() => registry.GetSnapshot().Warnings.Count > 0 && provider.Board.Clock.Waits == 2);

        Assert.Empty(registry.Threads);
        Assert.Equal(0, discovered.Sum(thread => thread.Starts));
        Assert.Empty(store.Current.ThreadUrls);
        Assert.Equal(new[] { BoardUrl }, store.Current.BoardUrls);

        store.SaveFailure = null;
        provider.Board.Clock.Advance();
        await TestWait.UntilAsync(() => registry.Threads.Count == 1 && discovered.Sum(thread => thread.Starts) == 1);
        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
        Assert.Equal(directory.Path, store.Current.DownloadDirectories[ThreadUrl]);
    }

    [Fact]
    public async Task ChangedRootAppliesToNewWatchesWhileExistingBoardDescendantsKeepAssignedRoot()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        string oldRoot = Path.Combine(directory.Path, "first-root");
        string newRoot = Path.Combine(directory.Path, "second-root");
        SessionStore store = new SessionStore(directory.Path, lease);
        await using Registry registry = CreateRegistry(provider, oldRoot, store);
        await registry.RestoreAsync();
        await registry.AddAsync(BoardUrl);
        IThread existing = (await registry.AddAsync(ThreadUrl)).Thread!;
        await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 1);

        await registry.ApplyOptionsAsync(Options(newRoot), persistSession: true);
        IThread direct = (await registry.AddAsync(OtherThreadUrl)).Thread!;
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>([provider.GetThread(new Uri(SuppressedThreadUrl))]);
        provider.Board.Clock.Advance();
        await TestWait.UntilAsync(() => registry.Threads.Count == 3 && registry.Threads.All(thread => ((ProbeThread)thread).Clock.Waits >= 1));
        IThread descendant = Assert.Single(registry.Threads, thread => thread.ThreadId == "789");
        SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;

        Assert.Equal(Path.Combine(oldRoot, "stub", "g", "123"), existing.DownloadDirectory);
        Assert.Equal(Path.Combine(oldRoot, "stub", "g", "789"), descendant.DownloadDirectory);
        Assert.Equal(Path.Combine(newRoot, "stub", "g", "456"), direct.DownloadDirectory);
        Assert.Equal(Path.Combine(oldRoot, "stub", "g"), registry.GetDownloadDirectory(provider.Board));
        Assert.Equal(oldRoot, durable.DownloadDirectories[BoardUrl]);
        Assert.Equal(oldRoot, durable.DownloadDirectories[ThreadUrl]);
        Assert.Equal(oldRoot, durable.DownloadDirectories[SuppressedThreadUrl]);
        Assert.Equal(newRoot, durable.DownloadDirectories[OtherThreadUrl]);
    }

    [Fact]
    public async Task EnablingPersistenceCommitsTheCompleteCurrentSession()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        await using Registry registry = CreateRegistry(provider, directory.Path, store, persistSession: false);
        await registry.AddAsync(BoardUrl);
        await registry.AddAsync(ThreadUrl);
        IThread removed = (await registry.AddAsync(SuppressedThreadUrl)).Thread!;
        await registry.RemoveThreadAsync(removed);
        Assert.Equal(0, store.SaveCalls);

        await registry.ApplyOptionsAsync(Options(directory.Path), persistSession: true);

        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
        Assert.Equal(new[] { BoardUrl }, store.Current.BoardUrls);
        Assert.Equal(new[] { SuppressedThreadUrl }, store.Current.SuppressedThreadUrls);
        Assert.Equal(directory.Path, store.Current.DownloadDirectories[ThreadUrl]);
        Assert.Equal(directory.Path, store.Current.DownloadDirectories[BoardUrl]);
    }

    [Fact]
    public async Task FailedPersistenceEnableDoesNotPartiallyApplyQueueLimitsRootOrFlag()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        DownloadQueue queue = new DownloadQueue(1, InitialBandwidth);
        string nextRoot = Path.Combine(directory.Path, "uncommitted-root");
        await using Registry registry = CreateRegistry(provider, directory.Path, store, persistSession: false, queue: queue);
        await registry.AddAsync(ThreadUrl);
        store.SaveFailure = new IOException("Synthetic settings commit failure");

        await Assert.ThrowsAsync<IOException>(() => registry.ApplyOptionsAsync(Options(nextRoot, 3, InitialBandwidth * 2), persistSession: true));

        Assert.Equal(1, queue.MaxParallelDownloads);
        Assert.Equal(InitialBandwidth, queue.MaxBandwidthBytesPerSecond);
        int failedSaves = store.SaveCalls;
        IThread next = (await registry.AddAsync(OtherThreadUrl)).Thread!;
        Assert.Equal(Path.Combine(directory.Path, "stub", "g", "456"), next.DownloadDirectory);
        Assert.Equal(failedSaves, store.SaveCalls);
    }

    [Fact]
    public async Task FailedEnabledSessionSaveDoesNotCommitSettingsOrApplyLiveOptions()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        DownloadQueue queue = new DownloadQueue(1, InitialBandwidth);
        SessionOptions configuration = Options(directory.Path, 1, InitialBandwidth);
        SessionOptions candidate = Options(Path.Combine(directory.Path, "uncommitted-settings-root"), 3, InitialBandwidth * 2);
        await using Registry registry = CreateRegistry(provider, directory.Path, store, queue: queue);
        await registry.RestoreAsync();
        await registry.AddAsync(ThreadUrl);
        store.SaveFailure = new IOException("Synthetic enabled-session commit failure");
        int settingsCommits = 0;
        Action commitSettings = () =>
        {
            settingsCommits++;
            configuration = candidate;
        };

        await Assert.ThrowsAsync<IOException>(() => registry.ApplyOptionsAsync(
            candidate,
            persistSession: true, commitSettings: commitSettings));

        Assert.Equal(0, settingsCommits);
        Assert.Equal(directory.Path, configuration.DownloadPath);
        Assert.Equal(1, configuration.MaxParallelDownloads);
        Assert.Equal(InitialBandwidth, configuration.BandwidthBytesPerSecond);
        Assert.Equal(1, queue.MaxParallelDownloads);
        Assert.Equal(InitialBandwidth, queue.MaxBandwidthBytesPerSecond);
        store.SaveFailure = null;
        IThread next = (await registry.AddAsync(OtherThreadUrl)).Thread!;
        Assert.Equal(Path.Combine(directory.Path, "stub", "g", "456"), next.DownloadDirectory);
    }

    [Fact]
    public async Task FailedSettingsCallbackRestoresPreviousDurableSessionAndLeavesLiveOptionsUnchanged()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        string previousRoot = Path.Combine(directory.Path, "previous-profile-root");
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot
        {
            ThreadUrls = [OtherThreadUrl],
            SuppressedThreadUrls = [SuppressedThreadUrl],
            DownloadDirectories = new Dictionary<string, string> { [OtherThreadUrl] = previousRoot }
        });
        DownloadQueue queue = new DownloadQueue(1, InitialBandwidth);
        await using Registry registry = CreateRegistry(provider, directory.Path, store, persistSession: false, queue: queue);
        await registry.AddAsync(ThreadUrl);
        int settingsCommits = 0;
        Action commitSettings = () =>
        {
            settingsCommits++;
            throw new IOException("Synthetic settings file write failure");
        };

        await Assert.ThrowsAsync<IOException>(() => registry.ApplyOptionsAsync(
            Options(Path.Combine(directory.Path, "uncommitted-root"), 3, InitialBandwidth * 2),
            persistSession: true, commitSettings: commitSettings));

        Assert.Equal(1, settingsCommits);
        Assert.Equal(new[] { OtherThreadUrl }, store.Current.ThreadUrls);
        Assert.Equal(new[] { SuppressedThreadUrl }, store.Current.SuppressedThreadUrls);
        Assert.Equal(previousRoot, store.Current.DownloadDirectories[OtherThreadUrl]);
        Assert.Equal(1, queue.MaxParallelDownloads);
        Assert.Equal(InitialBandwidth, queue.MaxBandwidthBytesPerSecond);
        Assert.Equal("123", Assert.Single(registry.Threads).ThreadId);
        int savesAfterCompensation = store.SaveCalls;
        IThread next = (await registry.AddAsync(OtherThreadUrl)).Thread!;
        Assert.Equal(Path.Combine(directory.Path, "stub", "g", "456"), next.DownloadDirectory);
        Assert.Equal(savesAfterCompensation, store.SaveCalls);
    }

    [Fact]
    public async Task SettingsCallbackAndCompensationFailureReportBothWithoutApplyingLiveOptions()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore(new SessionSnapshot { ThreadUrls = [OtherThreadUrl] });
        DownloadQueue queue = new DownloadQueue(1, InitialBandwidth);
        await using Registry registry = CreateRegistry(provider, directory.Path, store, persistSession: false, queue: queue);
        await registry.AddAsync(ThreadUrl);
        int previousSaves = store.SaveCalls;
        store.BeforeSaveAsync = _ => store.SaveCalls == previousSaves + 2
            ? Task.FromException(new IOException("Synthetic settings compensation write failure"))
            : Task.CompletedTask;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => registry.ApplyOptionsAsync(
            Options(Path.Combine(directory.Path, "uncommitted-root"), 3, InitialBandwidth * 2),
            persistSession: true, commitSettings: () => throw new InvalidOperationException("Synthetic settings callback failure")));

        Assert.Contains(failure.Flatten().InnerExceptions, error => error is InvalidOperationException);
        Assert.Contains(failure.Flatten().InnerExceptions, error => error is IOException);
        Assert.Equal(1, queue.MaxParallelDownloads);
        Assert.Equal(InitialBandwidth, queue.MaxBandwidthBytesPerSecond);
        Assert.Equal(Path.Combine(directory.Path, "stub", "g", "123"), Assert.Single(registry.Threads).DownloadDirectory);
        Assert.Equal(new[] { ThreadUrl }, store.Current.ThreadUrls);
    }

    [Fact]
    public async Task DisablingPersistencePreservesThePreviouslySavedSession()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        SessionStore store = new SessionStore(directory.Path, lease);
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        IThread thread = (await registry.AddAsync(ThreadUrl)).Thread!;
        string filePath = Path.Combine(directory.Path, "saved_urls.json");
        string saved = File.ReadAllText(filePath);

        await registry.ApplyOptionsAsync(Options(directory.Path), persistSession: false);
        await registry.RemoveThreadAsync(thread);
        await registry.AddAsync(OtherThreadUrl);
        await registry.StopAsync();

        Assert.Equal(saved, File.ReadAllText(filePath));
        SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;
        Assert.Equal(new[] { ThreadUrl }, durable.ThreadUrls);
        Assert.Empty(durable.SuppressedThreadUrls);
    }

    [Fact]
    public async Task ParallelAcknowledgedAdmissionsRetainEveryCommittedWatch()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new StubImageBoard();
        SessionStore store = new SessionStore(directory.Path, lease);
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        string[] urls = Enumerable.Range(100, 16).Select(id => $"https://stub.test/g/thread/{id}").ToArray();

        AdmissionResult[] admitted = await Task.WhenAll(urls.Select(url => registry.AddAsync(url)));
        SessionSnapshot durable = (await new SessionStore(directory.Path, lease).LoadAsync()).Snapshot;

        Assert.All(admitted, result => Assert.Equal(AdmissionStatus.Added, result.Status));
        Assert.Equal(urls.Order(), durable.ThreadUrls.Order());
        Assert.Equal(urls.Length, registry.Threads.Count);
        Assert.All(urls, url => Assert.Equal(directory.Path, durable.DownloadDirectories[url]));
    }

    [Fact]
    public async Task ConcurrentAdmissionsWaitForTheCurrentCommitAndUseItsCompleteSnapshot()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        ScriptedSessionStore store = new ScriptedSessionStore();
        await using Registry registry = CreateRegistry(provider, directory.Path, store);
        await registry.RestoreAsync();
        int initialSaves = store.SaveCalls;
        TaskCompletionSource saving = NewCompletion();
        TaskCompletionSource commit = NewCompletion();
        store.BeforeSaveAsync = _ =>
        {
            if (store.SaveCalls != initialSaves + 1) return Task.CompletedTask;
            saving.TrySetResult();
            return commit.Task;
        };
        Task<AdmissionResult> first = registry.AddAsync(ThreadUrl);
        await saving.Task.WaitAsync(TestWait.Timeout);
        Task<AdmissionResult> second = registry.AddAsync(OtherThreadUrl);
        Task<AdmissionResult> board = registry.AddAsync(BoardUrl);
        Task<AdmissionResult[]> all = Task.WhenAll(first, second, board);
        try
        {
            Assert.Equal(initialSaves + 1, store.SaveCalls);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(board.IsCompleted);
            commit.TrySetResult();
            Assert.All(await all.WaitAsync(TestWait.Timeout), admission => Assert.Equal(AdmissionStatus.Added, admission.Status));

            Assert.Equal(new[] { ThreadUrl, OtherThreadUrl }.Order(), store.Current.ThreadUrls.Order());
            Assert.Equal(new[] { BoardUrl }, store.Current.BoardUrls);
            Assert.Equal(3, store.Current.DownloadDirectories.Count);
        }
        finally
        {
            commit.TrySetResult();
            await all.WaitAsync(TestWait.Timeout);
        }
    }

    private static Registry CreateRegistry(StubImageBoard provider, string root, ISessionStore store,
        bool persistSession = true, DownloadQueue? queue = null)
        => new Registry([provider], queue ?? new DownloadQueue(1, InitialBandwidth), root,
            TimeSpan.FromMinutes(1), 60, sessionStore: store, persistSession: persistSession);

    private static async Task<int> WaitForCrashAcknowledgementAsync(Process child, string marker)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (child.HasExited)
                throw new InvalidOperationException($"Registry crash subprocess exited early with code {child.ExitCode}.");
            if (File.Exists(marker))
            {
                try
                {
                    string acknowledgement = await File.ReadAllTextAsync(marker);
                    if (acknowledgement.StartsWith(CrashAcknowledgement, StringComparison.Ordinal) &&
                        int.TryParse(acknowledgement[CrashAcknowledgement.Length..], out int processId) && processId > 0)
                        return processId;
                }
                catch (IOException)
                {
                    // The writer may still hold the newly created marker open.
                }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The Registry crash subprocess never acknowledged its committed mutations.");
    }

    private static async Task KillProbeProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync().WaitAsync(TestWait.Timeout);
    }

    private static SessionOptions Options(string root, int workers = 1, long bandwidth = InitialBandwidth)
        => new SessionOptions
        {
            DownloadPath = root,
            RecheckInterval = TimeSpan.FromMinutes(1),
            MaxParallelDownloads = workers,
            BandwidthBytesPerSecond = bandwidth
        };

    private static TaskCompletionSource NewCompletion() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewCompletion<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

    private class RecordingThread(IBoard board, Uri uri) : ProbeThread(board, uri), IThread
    {
        private int _starts;
        private int _stops;
        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        void IThread.StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
            => StartRecorded(queue, outputDirectory, interval);
        protected virtual void StartRecorded(DownloadQueue queue, string outputDirectory, TimeSpan interval)
        {
            Interlocked.Increment(ref _starts);
            base.StartMonitoring(queue, outputDirectory, interval);
        }
        Task IThread.StopMonitoringAsync()
        {
            Interlocked.Increment(ref _stops);
            return base.StopMonitoringAsync();
        }
    }

    private sealed class SetupFailureThread(IBoard board, Uri uri) : RecordingThread(board, uri)
    {
        protected override void StartRecorded(DownloadQueue queue, string outputDirectory, TimeSpan interval)
        {
            base.StartRecorded(queue, outputDirectory, interval);
            throw new InvalidOperationException("Synthetic setup failure after monitoring started");
        }
    }

    private sealed class RecordingBoard(IImageBoard provider) : ProbeBoard(provider), IBoard
    {
        private int _starts;
        private int _stops;
        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        void IBoard.StartMonitoring(int intervalInSeconds)
        {
            Interlocked.Increment(ref _starts);
            base.StartMonitoring(intervalInSeconds);
        }
        Task IBoard.StopMonitoringAsync()
        {
            Interlocked.Increment(ref _stops);
            return base.StopMonitoringAsync();
        }
    }

    private sealed class CountingSessionStore(ISessionStore inner) : ISessionStore
    {
        private int _saveCalls;
        public int SaveCalls => Volatile.Read(ref _saveCalls);
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCalls);
            return inner.SaveAsync(snapshot, cancellationToken);
        }
    }

    private sealed class ScriptedSessionStore : ISessionStore
    {
        private readonly object _sync = new object();
        private SessionSnapshot _current;
        private int _loadCalls;
        private int _saveCalls;
        public ScriptedSessionStore(SessionSnapshot? initial = null) => _current = Copy(initial ?? new SessionSnapshot());
        public int LoadCalls => Volatile.Read(ref _loadCalls);
        public int SaveCalls => Volatile.Read(ref _saveCalls);
        public SessionSnapshot Current { get { lock (_sync) { return Copy(_current); } } }
        public ConcurrentQueue<SessionSnapshot> SavedSnapshots { get; } = new ConcurrentQueue<SessionSnapshot>();
        public Func<Task>? BeforeLoadAsync { get; set; }
        public Func<SessionSnapshot, Task>? BeforeSaveAsync { get; set; }
        public Exception? SaveFailure { get; set; }
        public Exception? LoadFailure { get; set; }
        public string? LoadWarning { get; set; }
        public bool Recovered { get; set; }

        public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _loadCalls);
            if (BeforeLoadAsync != null) await BeforeLoadAsync().WaitAsync(cancellationToken);
            if (LoadFailure != null) throw LoadFailure;
            return new SessionLoadResult(Current, LoadWarning, Recovered);
        }

        public async Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionSnapshot captured = Copy(snapshot);
            Interlocked.Increment(ref _saveCalls);
            if (BeforeSaveAsync != null) await BeforeSaveAsync(captured).WaitAsync(cancellationToken);
            if (SaveFailure != null) throw SaveFailure;
            lock (_sync) { _current = captured; }
            SavedSnapshots.Enqueue(Copy(captured));
        }

        private static SessionSnapshot Copy(SessionSnapshot snapshot)
            => new SessionSnapshot
            {
                SchemaVersion = snapshot.SchemaVersion,
                ThreadUrls = snapshot.ThreadUrls.ToArray(),
                BoardUrls = snapshot.BoardUrls.ToArray(),
                SuppressedThreadUrls = snapshot.SuppressedThreadUrls.ToArray(),
                DownloadDirectories = new Dictionary<string, string>(snapshot.DownloadDirectories, StringComparer.Ordinal)
            };
    }
}

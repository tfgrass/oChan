using System.Diagnostics;
using System.Text.Json;
using oChan.Persistence;

namespace oChan.Tests;

public sealed class SessionStoreTests
{
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";
    private const string BoardUrl = "https://boards.4chan.org/g/";
    private const string SuppressedUrl = "https://boards.4chan.org/g/thread/456";
    private const string ProbeProfileVariable = "OCHAN_SESSION_LEASE_PROBE_PATH";
    private const string ProbeAvailableVariable = "OCHAN_SESSION_LEASE_PROBE_AVAILABLE";

    [Fact]
    public async Task MissingSessionLoadsEmptyWithoutCreatingSessionFiles()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);

        SessionLoadResult result = await store.LoadAsync();

        Assert.False(result.Recovered);
        Assert.Null(result.Warning);
        Assert.Empty(result.Snapshot.ThreadUrls);
        Assert.Empty(result.Snapshot.BoardUrls);
        Assert.Empty(result.Snapshot.SuppressedThreadUrls);
        Assert.Empty(result.Snapshot.DownloadDirectories);
        Assert.False(File.Exists(SessionPath(directory)));
        Assert.False(File.Exists(SessionPath(directory) + ".bak"));
    }

    [Fact]
    public async Task SaveRoundTripsSuppressionAndAssignedRootsWithPreviousSnapshotBackup()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        SessionSnapshot initial = FullSnapshot(directory.Path);
        await store.SaveAsync(initial);
        string original = File.ReadAllText(SessionPath(directory));

        SessionSnapshot changed = new SessionSnapshot
        {
            BoardUrls = new[] { BoardUrl },
            SuppressedThreadUrls = new[] { ThreadUrl, SuppressedUrl },
            DownloadDirectories = new Dictionary<string, string> { [BoardUrl] = directory.Path }
        };
        await store.SaveAsync(changed);
        SessionLoadResult loaded = await store.LoadAsync();

        Assert.False(loaded.Recovered);
        Assert.Null(loaded.Warning);
        Assert.Empty(loaded.Snapshot.ThreadUrls);
        Assert.Equal(changed.BoardUrls, loaded.Snapshot.BoardUrls);
        Assert.Equal(changed.SuppressedThreadUrls, loaded.Snapshot.SuppressedThreadUrls);
        Assert.Equal(directory.Path, loaded.Snapshot.DownloadDirectories[BoardUrl]);
        Assert.Equal(original, File.ReadAllText(SessionPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SaveCapturesCollectionsBeforeReturningTheTask()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        SessionSnapshot snapshot = FullSnapshot(directory.Path);

        Task save = store.SaveAsync(snapshot);
        snapshot.ThreadUrls[0] = SuppressedUrl;
        snapshot.DownloadDirectories.Clear();
        await save;

        SessionLoadResult loaded = await store.LoadAsync();
        Assert.Equal(new[] { ThreadUrl }, loaded.Snapshot.ThreadUrls);
        Assert.Equal(directory.Path, loaded.Snapshot.DownloadDirectories[ThreadUrl]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUrlListsLoadWithEmptyNewFields(bool hasSchemaVersion)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        string schemaProperty = hasSchemaVersion ? "\"SchemaVersion\":1," : string.Empty;
        string original = $"{{{schemaProperty}\"ThreadUrls\":[\"{ThreadUrl}\"],\"BoardUrls\":[\"{BoardUrl}\"]}}";
        File.WriteAllText(SessionPath(directory), original);

        SessionLoadResult loaded = await store.LoadAsync();

        Assert.False(loaded.Recovered, loaded.Warning);
        Assert.Null(loaded.Warning);
        Assert.Equal(1, loaded.Snapshot.SchemaVersion);
        Assert.Equal(new[] { ThreadUrl }, loaded.Snapshot.ThreadUrls);
        Assert.Equal(new[] { BoardUrl }, loaded.Snapshot.BoardUrls);
        Assert.Empty(loaded.Snapshot.SuppressedThreadUrls);
        Assert.Empty(loaded.Snapshot.DownloadDirectories);
        Assert.Equal(original, File.ReadAllText(SessionPath(directory)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"SchemaVersion\":2}")]
    [InlineData("{\"ThreadUrls\":null}")]
    [InlineData("{\"BoardUrls\":null}")]
    [InlineData("{\"SuppressedThreadUrls\":null}")]
    [InlineData("{\"DownloadDirectories\":null}")]
    [InlineData("{\"ThreadUrls\":[null]}")]
    [InlineData("{\"ThreadUrls\":[\"relative/thread\"]}")]
    [InlineData("{\"BoardUrls\":[\"file:///tmp/local\"]}")]
    [InlineData("{\"DownloadDirectories\":{\"https://boards.4chan.org/g/\":\"relative\"}}")]
    public async Task InvalidSessionUsesEmptyRecoveryWithoutChangingOriginal(string invalidJson)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        File.WriteAllText(SessionPath(directory), invalidJson);

        SessionLoadResult recovered = await store.LoadAsync();

        Assert.True(recovered.Recovered);
        Assert.NotNull(recovered.Warning);
        Assert.Empty(recovered.Snapshot.ThreadUrls);
        Assert.Empty(recovered.Snapshot.BoardUrls);
        Assert.Equal(invalidJson, File.ReadAllText(SessionPath(directory)));
        Assert.False(File.Exists(SessionPath(directory) + ".bak"));
    }

    [Fact]
    public async Task InvalidAbsoluteDownloadPathUsesRecoveryWithoutChangingOriginal()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        SessionSnapshot invalid = new SessionSnapshot
        {
            DownloadDirectories = new Dictionary<string, string>
            {
                [ThreadUrl] = directory.Path + Path.DirectorySeparatorChar + "invalid\0path"
            }
        };
        string original = JsonSerializer.Serialize(invalid);
        File.WriteAllText(SessionPath(directory), original);

        SessionLoadResult recovered = await store.LoadAsync();

        Assert.True(recovered.Recovered);
        Assert.NotNull(recovered.Warning);
        Assert.Empty(recovered.Snapshot.DownloadDirectories);
        Assert.Equal(original, File.ReadAllText(SessionPath(directory)));
    }

    [Fact]
    public async Task LockedPrimaryFailsLoadingInsteadOfRecoveringAnOlderBackup()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        await store.SaveAsync(FullSnapshot(directory.Path));
        await store.SaveAsync(new SessionSnapshot { ThreadUrls = new[] { SuppressedUrl } });
        string primary = File.ReadAllText(SessionPath(directory));
        string backup = File.ReadAllText(SessionPath(directory) + ".bak");

        using (FileStream locked = new FileStream(SessionPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
        }

        SessionLoadResult loaded = await store.LoadAsync();
        Assert.False(loaded.Recovered);
        Assert.Equal(new[] { SuppressedUrl }, loaded.Snapshot.ThreadUrls);
        Assert.Equal(primary, File.ReadAllText(SessionPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SessionPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedBackupFailsRecoveryInsteadOfActivatingEmptySession(bool missingPrimary)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        const string damaged = "{invalid";
        string backup = JsonSerializer.Serialize(FullSnapshot(directory.Path));
        if (!missingPrimary)
            File.WriteAllText(SessionPath(directory), damaged);
        File.WriteAllText(SessionPath(directory) + ".bak", backup);

        using (FileStream locked = new FileStream(SessionPath(directory) + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
        }

        SessionLoadResult recovered = await store.LoadAsync();
        Assert.True(recovered.Recovered);
        Assert.Equal(new[] { ThreadUrl }, recovered.Snapshot.ThreadUrls);
        if (missingPrimary)
            Assert.False(File.Exists(SessionPath(directory)));
        else
            Assert.Equal(damaged, File.ReadAllText(SessionPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SessionPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedPrimaryOrBackupCannotBeReplacedOrQuarantinedBySave(bool lockBackup)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        await store.SaveAsync(FullSnapshot(directory.Path));
        await store.SaveAsync(new SessionSnapshot { ThreadUrls = new[] { SuppressedUrl } });
        string primary = File.ReadAllText(SessionPath(directory));
        string backup = File.ReadAllText(SessionPath(directory) + ".bak");
        string lockedPath = SessionPath(directory) + (lockBackup ? ".bak" : string.Empty);

        using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(new SessionSnapshot()));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        Assert.Equal(primary, File.ReadAllText(SessionPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SessionPath(directory) + ".bak"));
        SessionLoadResult loaded = await store.LoadAsync();
        Assert.False(loaded.Recovered);
        Assert.Equal(new[] { SuppressedUrl }, loaded.Snapshot.ThreadUrls);
    }

    [Fact]
    public async Task CorruptPrimaryRecoversValidBackupAndExplicitSavePreservesDamagedInput()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        await store.SaveAsync(FullSnapshot(directory.Path));
        string original = File.ReadAllText(SessionPath(directory));
        await store.SaveAsync(new SessionSnapshot());
        const string damaged = "{partial-session";
        File.WriteAllText(SessionPath(directory), damaged);

        SessionLoadResult recovered = await store.LoadAsync();

        Assert.True(recovered.Recovered);
        Assert.Contains("Recovered the session from backup", recovered.Warning);
        Assert.Equal(new[] { ThreadUrl }, recovered.Snapshot.ThreadUrls);
        Assert.Equal(damaged, File.ReadAllText(SessionPath(directory)));
        await store.SaveAsync(recovered.Snapshot);

        Assert.Equal(original, File.ReadAllText(SessionPath(directory) + ".bak"));
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, "saved_urls.json.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(preserved));
        Assert.False((await store.LoadAsync()).Recovered);
    }

    [Fact]
    public async Task CorruptPrimaryAndBackupRemainUntouchedWhenUsingEmptyRecovery()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        File.WriteAllText(SessionPath(directory), "null");
        File.WriteAllText(SessionPath(directory) + ".bak", "{broken-backup");

        SessionLoadResult recovered = await store.LoadAsync();

        Assert.True(recovered.Recovered);
        Assert.NotNull(recovered.Warning);
        Assert.Empty(recovered.Snapshot.ThreadUrls);
        Assert.Equal("null", File.ReadAllText(SessionPath(directory)));
        Assert.Equal("{broken-backup", File.ReadAllText(SessionPath(directory) + ".bak"));
    }

    [Fact]
    public async Task InvalidNewSnapshotCannotOverwriteExistingSession()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        await store.SaveAsync(FullSnapshot(directory.Path));
        string original = File.ReadAllText(SessionPath(directory));

        SessionSnapshot invalid = new SessionSnapshot { ThreadUrls = new[] { "not-a-url" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid));

        Assert.Equal(original, File.ReadAllText(SessionPath(directory)));
        Assert.False(File.Exists(SessionPath(directory) + ".bak"));
    }

    [Fact]
    public async Task PreCancelledSaveDoesNotChangeCurrentOrBackupSnapshot()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        await store.SaveAsync(FullSnapshot(directory.Path));
        await store.SaveAsync(new SessionSnapshot { ThreadUrls = new[] { ThreadUrl } });
        string current = File.ReadAllText(SessionPath(directory));
        string backup = File.ReadAllText(SessionPath(directory) + ".bak");
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new SessionSnapshot(), cancellation.Token));

        Assert.Equal(current, File.ReadAllText(SessionPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SessionPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableSessionPathsPropagateAccessErrorsOnLoadAndSave(bool inaccessibleBackup)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        string unreadablePath = SessionPath(directory) + (inaccessibleBackup ? ".bak" : string.Empty);
        Directory.CreateDirectory(unreadablePath);
        string sentinelPath = Path.Combine(unreadablePath, "preserve.txt");
        File.WriteAllText(sentinelPath, "keep-me");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(FullSnapshot(directory.Path)));

        Assert.True(Directory.Exists(unreadablePath));
        Assert.Equal("keep-me", File.ReadAllText(sentinelPath));
        Assert.False(File.Exists(SessionPath(directory)));
        Assert.False(File.Exists(SessionPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void LeaseRejectsAnotherHandleUntilDisposedAndAllowsIndependentProfiles()
    {
        using TempDirectory directory = new TempDirectory();
        string firstProfile = Path.Combine(directory.Path, "first");
        string secondProfile = Path.Combine(directory.Path, "second");
        ProfileLease first = ProfileLease.Acquire(firstProfile);
        try
        {
            Assert.ThrowsAny<IOException>(() => ProfileLease.Acquire(firstProfile));
            using ProfileLease independent = ProfileLease.Acquire(secondProfile);
            Assert.Equal(secondProfile, independent.ProfileDirectory);
            Assert.Throws<ArgumentException>(() => new SessionStore(secondProfile, first));
        }
        finally
        {
            first.Dispose();
        }

        using ProfileLease reopened = ProfileLease.Acquire(firstProfile);
        Assert.Equal(firstProfile, reopened.ProfileDirectory);
    }

    [Fact]
    public async Task DisposedLeaseCannotBeUsedForFurtherStoreOperations()
    {
        using TempDirectory directory = new TempDirectory();
        ProfileLease lease = ProfileLease.Acquire(directory.Path);
        SessionStore store = new SessionStore(directory.Path, lease);
        lease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(new SessionSnapshot()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAsync());
        Assert.Throws<ObjectDisposedException>(() => new SessionStore(directory.Path, lease));
        Assert.False(File.Exists(SessionPath(directory)));
    }

    [Fact]
    public async Task LeaseIsExclusiveAcrossProcessesAndReleasesOnDispose()
    {
        using TempDirectory directory = new TempDirectory();
        ProfileLease lease = ProfileLease.Acquire(directory.Path);
        try
        {
            await RunLeaseProbeAsync(directory.Path, expectedAvailable: false);
        }
        finally
        {
            lease.Dispose();
        }

        await RunLeaseProbeAsync(directory.Path, expectedAvailable: true);
    }

    [Fact]
    public void ProfileLeaseChildProbe()
    {
        string? profile = Environment.GetEnvironmentVariable(ProbeProfileVariable);
        if (profile == null)
            return;

        bool available = Environment.GetEnvironmentVariable(ProbeAvailableVariable) == "true";
        if (available)
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
        }
        else
        {
            Assert.ThrowsAny<IOException>(() => ProfileLease.Acquire(profile));
            using ProfileLease independent = ProfileLease.Acquire(Path.Combine(profile, "independent-child-profile"));
        }
    }

    private static async Task RunLeaseProbeAsync(string profile, bool expectedAvailable)
    {
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(SessionStoreTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=oChan.Tests.SessionStoreTests.ProfileLeaseChildProbe");
        start.Environment[ProbeProfileVariable] = profile;
        start.Environment[ProbeAvailableVariable] = expectedAvailable ? "true" : "false";

        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the profile lease subprocess.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw new TimeoutException($"The profile lease subprocess timed out. {await output} {await errors}");
        }

        Assert.True(child.ExitCode == 0, $"Profile lease subprocess failed. {await output} {await errors}");
    }

    private static SessionSnapshot FullSnapshot(string downloadRoot)
    {
        return new SessionSnapshot
        {
            ThreadUrls = new[] { ThreadUrl },
            BoardUrls = new[] { BoardUrl },
            SuppressedThreadUrls = new[] { SuppressedUrl },
            DownloadDirectories = new Dictionary<string, string>
            {
                [ThreadUrl] = downloadRoot,
                [BoardUrl] = downloadRoot
            }
        };
    }

    private static string SessionPath(TempDirectory directory) => Path.Combine(directory.Path, "saved_urls.json");
}

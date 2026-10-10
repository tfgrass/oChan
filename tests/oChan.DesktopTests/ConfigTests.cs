using System.Text.Json;

namespace oChan.DesktopTests;

public sealed class ConfigTests
{
    [Fact]
    public void LoadingMissingProfileReturnsDefaultsWithoutCreatingDirectories()
    {
        using TempDirectory directory = new TempDirectory();
        string profile = Path.Combine(directory.Path, "not-created");

        Config config = Config.LoadConfig(profile);

        Assert.False(Directory.Exists(profile));
        Assert.Null(config.LoadWarning);
        Assert.Equal(60, config.RecheckTimer);
        Assert.Equal(5, config.MaxParallelDownloads);
        Assert.Equal(1024 * 1024, config.BandwidthLimiter);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "oChan"), config.DownloadPath);
        Assert.True(Path.IsPathFullyQualified(config.DownloadPath));
        config.Validate();
    }

    [Fact]
    public void SaveRoundTripsAndKeepsThePreviousValidSettingsAsBackup()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.DownloadPath = Path.Combine(directory.Path, "downloads");
        config.RecheckTimer = 120;
        config.SaveConfig();
        string original = File.ReadAllText(SettingsPath(directory));

        config.RecheckTimer = 180;
        config.MaxParallelDownloads = 7;
        config.SaveConfig();

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(180, loaded.RecheckTimer);
        Assert.Equal(7, loaded.MaxParallelDownloads);
        Assert.Equal(config.DownloadPath, loaded.DownloadPath);
        Assert.Equal(original, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"RecheckTimer\":0}")]
    [InlineData("{\"RecheckTimer\":9}")]
    [InlineData("{\"RecheckTimer\":4294968}")]
    [InlineData("{\"BandwidthLimiter\":0}")]
    [InlineData("{\"MaxParallelDownloads\":0}")]
    [InlineData("{\"DownloadPath\":null}")]
    [InlineData("{\"DownloadPath\":\" \"}")]
    [InlineData("{\"SchemaVersion\":2}")]
    public void InvalidSettingsUseDefaultsAndPreserveOriginal(string invalidJson)
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(SettingsPath(directory), invalidJson);

        Config config = Config.LoadConfig(directory.Path);

        Assert.NotNull(config.LoadWarning);
        Assert.Contains("Default settings", config.LoadWarning);
        Assert.Equal(60, config.RecheckTimer);
        Assert.Equal(5, config.MaxParallelDownloads);
        Assert.Equal(invalidJson, File.ReadAllText(SettingsPath(directory)));
        Assert.Single(Directory.GetFiles(directory.Path));
        config.Validate();
    }

    [Fact]
    public void CorruptPrimaryRecoversBackupAndAnExplicitSavePreservesBothRecoverySources()
    {
        using TempDirectory directory = new TempDirectory();
        Config original = Config.LoadConfig(directory.Path);
        original.RecheckTimer = 120;
        original.SaveConfig();
        original.RecheckTimer = 180;
        original.SaveConfig();
        string backup = File.ReadAllText(SettingsPath(directory) + ".bak");
        const string damaged = "{interrupted-write";
        File.WriteAllText(SettingsPath(directory), damaged);

        Config recovered = Config.LoadConfig(directory.Path);

        Assert.Equal(120, recovered.RecheckTimer);
        Assert.Contains("Recovered settings", recovered.LoadWarning);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));

        recovered.RecheckTimer = 240;
        recovered.SaveConfig();

        Assert.Null(recovered.LoadWarning);
        Assert.Equal(240, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, "settings.json.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(preserved));
        Assert.DoesNotContain("LoadWarning", File.ReadAllText(SettingsPath(directory)));
    }

    [Fact]
    public void MissingPrimaryCanRecoverBackupWithoutWritingThePrimary()
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(SettingsPath(directory) + ".bak", "{\"RecheckTimer\":120}");

        Config recovered = Config.LoadConfig(directory.Path);

        Assert.Equal(120, recovered.RecheckTimer);
        Assert.NotNull(recovered.LoadWarning);
        Assert.False(File.Exists(SettingsPath(directory)));
    }

    [Fact]
    public void LockedPrimaryFailsLoadingInsteadOfRecoveringAnOlderBackup()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.RecheckTimer = 120;
        config.SaveConfig();
        config.RecheckTimer = 180;
        config.SaveConfig();
        string primary = File.ReadAllText(SettingsPath(directory));
        string backup = File.ReadAllText(SettingsPath(directory) + ".bak");

        using (FileStream locked = new FileStream(SettingsPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Config.LoadConfig(directory.Path));
        }

        Assert.Equal(180, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Equal(primary, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public void LockedBackupFailsRecoveryInsteadOfActivatingDefaults()
    {
        using TempDirectory directory = new TempDirectory();
        const string damaged = "{invalid";
        const string backup = "{\"RecheckTimer\":120}";
        File.WriteAllText(SettingsPath(directory), damaged);
        File.WriteAllText(SettingsPath(directory) + ".bak", backup);

        using (FileStream locked = new FileStream(SettingsPath(directory) + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Config.LoadConfig(directory.Path));
        }

        Assert.Equal(120, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedPrimaryOrBackupCannotBeReplacedOrQuarantinedBySave(bool lockBackup)
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.RecheckTimer = 120;
        config.SaveConfig();
        config.RecheckTimer = 180;
        config.SaveConfig();
        string primary = File.ReadAllText(SettingsPath(directory));
        string backup = File.ReadAllText(SettingsPath(directory) + ".bak");
        string lockedPath = SettingsPath(directory) + (lockBackup ? ".bak" : string.Empty);
        config.RecheckTimer = 240;

        using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => config.SaveConfig());
            Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        Assert.Equal(primary, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Equal(180, Config.LoadConfig(directory.Path).RecheckTimer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableSettingsPathsPropagateAccessErrorsOnLoadAndSave(bool inaccessibleBackup)
    {
        using TempDirectory directory = new TempDirectory();
        string unreadablePath = SettingsPath(directory) + (inaccessibleBackup ? ".bak" : string.Empty);
        Directory.CreateDirectory(unreadablePath);
        Config config = new Config();

        Assert.Throws<UnauthorizedAccessException>(() => Config.LoadConfig(directory.Path));
        Assert.Throws<UnauthorizedAccessException>(() => config.SaveConfig(directory.Path));

        Assert.True(Directory.Exists(unreadablePath));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void SavingDefaultsAfterInvalidPrimaryKeepsTheInvalidDocumentForRecovery()
    {
        using TempDirectory directory = new TempDirectory();
        const string damaged = "null";
        File.WriteAllText(SettingsPath(directory), damaged);
        Config defaults = Config.LoadConfig(directory.Path);

        defaults.SaveConfig();

        Assert.Null(Config.LoadConfig(directory.Path).LoadWarning);
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, "settings.json.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(preserved));
    }

    [Fact]
    public void ReplacingAnInvalidBackupPreservesItsOriginalContents()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string valid = File.ReadAllText(SettingsPath(directory));
        const string damaged = "broken-backup";
        File.WriteAllText(SettingsPath(directory) + ".bak", damaged);

        config.RecheckTimer = 120;
        config.SaveConfig();

        Assert.Equal(valid, File.ReadAllText(SettingsPath(directory) + ".bak"));
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, "settings.json.bak.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(preserved));
    }

    [Fact]
    public void InvalidEditedValuesCannotOverwriteValidSettings()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string original = File.ReadAllText(SettingsPath(directory));
        config.MaxParallelDownloads = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() => config.SaveConfig());

        Assert.Equal(original, File.ReadAllText(SettingsPath(directory)));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(4294967)]
    public void SupportedRecheckTimerBoundariesRoundTrip(int seconds)
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.RecheckTimer = seconds;

        config.Validate();
        config.SaveConfig();

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(seconds, loaded.RecheckTimer);
    }

    [Theory]
    [InlineData(4294968)]
    [InlineData(int.MaxValue)]
    public void RecheckTimerAboveRuntimeMaximumCannotReplaceValidSettings(int seconds)
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string original = File.ReadAllText(SettingsPath(directory));
        config.RecheckTimer = seconds;

        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => config.SaveConfig());

        Assert.Equal(original, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(60, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void SaveFailureIsObservableAndDoesNotReplaceAnExistingPath()
    {
        using TempDirectory directory = new TempDirectory();
        string blockedProfile = Path.Combine(directory.Path, "profile-is-a-file");
        File.WriteAllText(blockedProfile, "keep-me");
        Config config = new Config();

        Assert.ThrowsAny<IOException>(() => config.SaveConfig(blockedProfile));

        Assert.Equal("keep-me", File.ReadAllText(blockedProfile));
    }

    [Fact]
    public void LegacyRelativeDownloadPathKeepsItsEffectiveLocationAndSavesAsAbsolute()
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(SettingsPath(directory), "{\"DownloadPath\":\"Downloads\"}");
        string expected = Path.GetFullPath("Downloads", Environment.CurrentDirectory);

        Config config = Config.LoadConfig(directory.Path);

        Assert.Equal(expected, config.DownloadPath);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(5, config.MaxParallelDownloads);
        config.SaveConfig();
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(expected, saved.RootElement.GetProperty("DownloadPath").GetString());
    }

    [Fact]
    public void CandidateCloneKeepsProfileBindingAndOnlyCopiesValidatedValues()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        Config candidate = config.Clone();
        candidate.RecheckTimer = 120;

        candidate.SaveConfig();

        Assert.Equal(60, config.RecheckTimer);
        Assert.Equal(120, Config.LoadConfig(directory.Path).RecheckTimer);
        config.CopyFrom(candidate);
        Assert.Equal(120, config.RecheckTimer);

        candidate.RecheckTimer = 0;
        candidate.DownloadPath = Path.Combine(directory.Path, "must-not-apply");
        Assert.Throws<ArgumentOutOfRangeException>(() => config.CopyFrom(candidate));
        Assert.Equal(120, config.RecheckTimer);
        Assert.NotEqual(candidate.DownloadPath, config.DownloadPath);
    }

    private static string SettingsPath(TempDirectory directory) => Path.Combine(directory.Path, "settings.json");
}

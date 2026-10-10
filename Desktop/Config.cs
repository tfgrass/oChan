using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using oChan.Models;
using oChan.Persistence;
using Serilog;

namespace oChan
{
    public class Config
    {
        private string? _profileDirectory;
        private string _relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);

        public int SchemaVersion { get; set; } = 1;
        public string DownloadPath { get; set; } = GetDefaultDownloadPath();
        public int RecheckTimer { get; set; } = 60;
        public bool SaveUrlsOnExit { get; set; } = true;
        public bool MinimizeToTray { get; set; }
        public long BandwidthLimiter { get; set; } = 1024 * 1024;
        public int MaxParallelDownloads { get; set; } = 5;

        [JsonIgnore]
        public string? LoadWarning { get; private set; }

        /// <summary>Resolves the profile without creating files or directories.</summary>
        public static string GetConfigDirectory()
        {
            string? overrideDirectory = Environment.GetEnvironmentVariable("OCHAN_PROFILE");
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
                return Path.GetFullPath(overrideDirectory);

            string homePath = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrWhiteSpace(homePath))
                throw new InvalidOperationException("The user configuration directory could not be resolved.");

            return Path.GetFullPath(Path.Combine(homePath, ".oChan"));
        }

        private static string GetDefaultDownloadPath()
        {
            string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(homePath))
                throw new InvalidOperationException("The user home directory could not be resolved.");

            return Path.GetFullPath(Path.Combine(homePath, "Downloads", "oChan"));
        }

        public static Config LoadConfig() => LoadConfig(GetConfigDirectory());

        /// <summary>Loads an isolated profile. Loading never changes existing files.</summary>
        public static Config LoadConfig(string profileDirectory)
        {
            string directory = ResolveProfileDirectory(profileDirectory);
            string filePath = Path.Combine(directory, "settings.json");
            string relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);
            LoadStatus status = TryRead(filePath, relativePathBaseDirectory, out Config? config, out string? error);
            if (status == LoadStatus.Valid)
                return BindProfile(config!, directory, relativePathBaseDirectory);

            string backupPath = filePath + ".bak";
            LoadStatus backupStatus = TryRead(backupPath, relativePathBaseDirectory, out Config? backup, out string? backupError);
            if (backupStatus == LoadStatus.Valid)
            {
                Config recovered = BindProfile(backup!, directory, relativePathBaseDirectory);
                recovered.LoadWarning = $"Settings could not be loaded from '{filePath}' ({error ?? "file missing"}). " +
                    $"Recovered settings from backup '{backupPath}'. The original files were preserved.";
                Log.Warning("{LoadWarning}", recovered.LoadWarning);
                return recovered;
            }

            Config defaults = BindProfile(new Config(), directory, relativePathBaseDirectory);
            if (status != LoadStatus.Missing || backupStatus != LoadStatus.Missing)
            {
                defaults.LoadWarning = $"Settings could not be loaded from '{filePath}' ({error ?? "file missing"}); " +
                    $"no valid backup was available ({backupError ?? "file missing"}). " +
                    "Default settings are in use. The original files were preserved.";
                Log.Warning("{LoadWarning}", defaults.LoadWarning);
            }

            return defaults;
        }

        public void Validate()
        {
            if (SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported settings schema version: {SchemaVersion}.");
            if (RecheckTimer < 10 || TimeSpan.FromSeconds(RecheckTimer).TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(RecheckTimer), "The recheck interval must be between 10 seconds and the supported timer maximum.");
            if (BandwidthLimiter <= 0)
                throw new ArgumentOutOfRangeException(nameof(BandwidthLimiter), "The bandwidth limit must be greater than zero.");
            if (MaxParallelDownloads <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxParallelDownloads), "Parallel downloads must be greater than zero.");
            if (string.IsNullOrWhiteSpace(DownloadPath))
                throw new ArgumentException("A download directory is required.", nameof(DownloadPath));

            _ = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory);
        }

        public Config Clone()
        {
            return new Config
            {
                SchemaVersion = SchemaVersion,
                DownloadPath = DownloadPath,
                RecheckTimer = RecheckTimer,
                SaveUrlsOnExit = SaveUrlsOnExit,
                MinimizeToTray = MinimizeToTray,
                BandwidthLimiter = BandwidthLimiter,
                MaxParallelDownloads = MaxParallelDownloads,
                LoadWarning = LoadWarning,
                _profileDirectory = _profileDirectory,
                _relativePathBaseDirectory = _relativePathBaseDirectory
            };
        }

        /// <summary>Applies validated values without changing this instance's profile.</summary>
        public void CopyFrom(Config source)
        {
            ArgumentNullException.ThrowIfNull(source);
            source.Validate();
            string absoluteDownloadPath = Path.GetFullPath(source.DownloadPath, source._relativePathBaseDirectory);

            SchemaVersion = source.SchemaVersion;
            DownloadPath = absoluteDownloadPath;
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            MinimizeToTray = source.MinimizeToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            LoadWarning = source.LoadWarning;
        }

        public void SaveConfig() => SaveConfig(_profileDirectory ?? GetConfigDirectory());

        /// <summary>Atomically saves settings; validation and write errors reach the caller.</summary>
        public void SaveConfig(string profileDirectory)
        {
            Validate();
            string directory = ResolveProfileDirectory(profileDirectory);
            string filePath = Path.Combine(directory, "settings.json");
            DownloadPath = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory);
            string json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.Config);

            LoadStatus currentStatus = TryRead(filePath, _relativePathBaseDirectory, out _, out _);
            string backupPath = filePath + ".bak";
            LoadStatus backupStatus = TryRead(backupPath, _relativePathBaseDirectory, out _, out _);
            if (currentStatus == LoadStatus.Valid && backupStatus == LoadStatus.Invalid)
                AtomicJsonFile.PreserveFile(backupPath);

            AtomicJsonFile.WriteAllText(filePath, json, backupCurrentFile: currentStatus == LoadStatus.Valid);
            _profileDirectory = directory;
            LoadWarning = null;
            Log.Information("Configuration saved to {ConfigFilePath}", filePath);
        }

        public void PrintConfig()
        {
            Log.Information("Download Path: {DownloadPath}", DownloadPath);
            Log.Information("Recheck Timer: {RecheckTimer} seconds", RecheckTimer);
            Log.Information("Save URLs on Exit: {SaveUrlsOnExit}", SaveUrlsOnExit);
            Log.Information("Minimize to Tray: {MinimizeToTray}", MinimizeToTray);
            Log.Information("Bandwidth Limiter: {BandwidthLimiter} bytes per second", BandwidthLimiter);
            Log.Information("Parallel downloads: {MaxParallelDownloads}", MaxParallelDownloads);
        }

        private static Config BindProfile(Config config, string directory, string relativePathBaseDirectory)
        {
            config._profileDirectory = directory;
            config._relativePathBaseDirectory = relativePathBaseDirectory;
            return config;
        }

        private static string ResolveProfileDirectory(string profileDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
            return Path.GetFullPath(profileDirectory);
        }

        private static LoadStatus TryRead(string filePath, string relativePathBaseDirectory, out Config? config, out string? error)
        {
            config = null;
            error = null;
            string json;
            try
            {
                json = File.ReadAllText(filePath);
            }
            catch (FileNotFoundException)
            {
                return LoadStatus.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return LoadStatus.Missing;
            }

            // An inaccessible document is not a damaged document. Propagate read
            // failures so temporary locks or permissions cannot activate defaults
            // or allow SaveConfig to replace a file it could not inspect.
            try
            {
                config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config);
                if (config == null)
                    throw new InvalidDataException("The settings document contains null.");

                config._relativePathBaseDirectory = relativePathBaseDirectory;
                config.Validate();
                // Existing relative settings keep the location used by the old application.
                // Resolve once on load so later working-directory changes cannot move jobs.
                config.DownloadPath = Path.GetFullPath(config.DownloadPath, relativePathBaseDirectory);
                return LoadStatus.Valid;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                config = null;
                error = ex.Message;
                return LoadStatus.Invalid;
            }
        }

        private enum LoadStatus { Missing, Valid, Invalid }
    }

    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(SavedUrls))]
    public partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}

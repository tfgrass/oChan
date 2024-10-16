// File: ./Config.cs
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using oChan.Models; // Stellen Sie sicher, dass dieser Namespace importiert ist

namespace oChan
{
    public class Config
    {
        public string DownloadPath { get; set; }
        public int RecheckTimer { get; set; } // Timer in seconds
        public bool SaveUrlsOnExit { get; set; }
        public bool MinimizeToTray { get; set; }
        public long BandwidthLimiter { get; set; } // Bytes per second (convert later)

        private static readonly string ConfigFilePath = Path.Combine(GetConfigDirectory(), "settings.json");

        public Config()
        {
            // Set default values
            DownloadPath = GetDefaultDownloadPath();
            RecheckTimer = 60; // Default to 1 minute
            SaveUrlsOnExit = true;
            MinimizeToTray = false;
            BandwidthLimiter = 1024 * 1024; // Default to 1 MB/s
        }

        public static string GetConfigDirectory()
        {
            string homePath;
            if (OperatingSystem.IsWindows())
            {
                homePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported OS");
            }

            string configDir = Path.Combine(homePath, ".oChan");
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            return configDir;
        }

        private string GetDefaultDownloadPath()
        {
            // Provide OS-specific download paths
            string baseDirectory = AppContext.BaseDirectory;
            return Path.Combine("Downloads");
        }

        public static Config LoadConfig()
        {
            if (File.Exists(ConfigFilePath))
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        TypeInfoResolver = ConfigJsonContext.Default
                    };
                    string json = File.ReadAllText(ConfigFilePath);
                    return JsonSerializer.Deserialize<Config>(json, options);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to load configuration file.");
                    throw;
                }
            }

            return new Config(); // Return default config if none exists
        }

        public void SaveConfig()
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    TypeInfoResolver = ConfigJsonContext.Default
                };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigFilePath, json);
                Log.Information("Configuration saved to {ConfigFilePath}", ConfigFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save configuration.");
            }
        }

        public void PrintConfig()
        {
            Log.Information("Download Path: {DownloadPath}", DownloadPath);
            Log.Information("Recheck Timer: {RecheckTimer} seconds", RecheckTimer);
            Log.Information("Save URLs on Exit: {SaveUrlsOnExit}", SaveUrlsOnExit);
            Log.Information("Minimize to Tray: {MinimizeToTray}", MinimizeToTray);
            Log.Information("Bandwidth Limiter: {BandwidthLimiter} bytes per second", BandwidthLimiter);
        }
    }

    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(SavedUrls))] // Fügen Sie SavedUrls hier hinzu
    public partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}

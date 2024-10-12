using System;
using System.IO;
using Serilog;

namespace oChan
{
    public class Config
    {
        public string DownloadPath { get; set; }

        // Constructor with a default download path
        public Config()
        {
            Log.Debug("Initializing Config instance.");

            // Initially hardcoded, but later read from config file
            DownloadPath = GetDefaultPath();
        }

        private string GetDefaultPath()
        {
            try
            {
                string path;
                // Get a default path based on the OS
                if (OperatingSystem.IsWindows())
                {
                    path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "oChanDownloads");
                    Log.Debug("Default download path for Windows: {DownloadPath}", path);
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "oChanDownloads");
                    Log.Debug("Default download path for Unix-based OS: {DownloadPath}", path);
                }
                else
                {
                    Log.Error("Unsupported operating system detected.");
                    throw new PlatformNotSupportedException("Unsupported OS");
                }
                return path;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred while getting the default download path.");
                throw;
            }
        }

        // Method to print configuration settings (for testing)
        public void PrintConfig()
        {
            Log.Information("Configuration settings:");
            Log.Information("Download Path: {DownloadPath}", DownloadPath);
        }

        // Later, add methods to save/load from a config file
    }
}

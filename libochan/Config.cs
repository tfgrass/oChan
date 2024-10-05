using System;

namespace oChan
{
    public class Config
    {
        public string DownloadPath { get; set; }

        // Constructor with a default download path
        public Config()
        {
            // Initially hardcoded, but later read from config file
            DownloadPath = GetDefaultPath();
        }

        private string GetDefaultPath()
        {
            // Get a default path based on the OS
            if (OperatingSystem.IsWindows())
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\oChanDownloads";
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/oChanDownloads";
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported OS");
            }
        }

        // Method to print configuration settings (for testing)
        public void PrintConfig()
        {
            Console.WriteLine($"Download Path: {DownloadPath}");
        }

        // Later, add methods to save/load from a config file
    }
}

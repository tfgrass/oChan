using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.IO;

namespace oChan
{
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private Config _config;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SettingsWindow(Config config)
        {
            InitializeComponent();
            _config = config;
            DataContext = this; // Set DataContext to the window instance

            // Initialize the bindings to the config settings
            DownloadPath = _config.DownloadPath ?? GetDefaultDownloadPath();
            RecheckTimer = _config.RecheckTimer > 0 ? _config.RecheckTimer : 60;
            SaveUrlsOnExit = _config.SaveUrlsOnExit;
            MinimizeToTray = _config.MinimizeToTray;
            BandwidthLimiterMB = _config.BandwidthLimiter > 0 ? _config.BandwidthLimiter / (1024.0 * 1024.0) : 5.0; // Default to 5 MB/s
        }

        // Properties for data binding
        public string DownloadPath { get; set; }
        public int RecheckTimer { get; set; }
        public bool SaveUrlsOnExit { get; set; }
        public bool MinimizeToTray { get; set; }
        public double BandwidthLimiterMB { get; set; } // In MB/s

        // Default download path
        private string GetDefaultDownloadPath() => Path.Combine(Directory.GetCurrentDirectory(), "Downloads");

        private void OnBrowseButtonClick(object sender, RoutedEventArgs e)
        {
            // Open a file picker to choose the download path
            var dialog = new OpenFolderDialog
            {
                Title = "Select Download Folder"
            };

            dialog.ShowAsync(this).ContinueWith(t =>
            {
                if (t.Result != null)
                {
                    DownloadPath = t.Result;
                    OnPropertyChanged(nameof(DownloadPath));
                }
            });
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            // Save the settings to the config instance
            _config.DownloadPath = DownloadPath;
            _config.RecheckTimer = RecheckTimer;
            _config.SaveUrlsOnExit = SaveUrlsOnExit;
            _config.MinimizeToTray = MinimizeToTray;
            _config.BandwidthLimiter = (long)(BandwidthLimiterMB * 1024 * 1024); // Convert MB back to bytes

            // Save the config to disk
            _config.SaveConfig();

            // Close the window
            Close();
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

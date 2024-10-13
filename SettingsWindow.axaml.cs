using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;

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
            DownloadPath = _config.DownloadPath;
            RecheckTimer = _config.RecheckTimer;
            SaveUrlsOnExit = _config.SaveUrlsOnExit;
            MinimizeToTray = _config.MinimizeToTray;
            BandwidthLimiterMB = _config.BandwidthLimiter / (1024.0 * 1024.0); // Convert bytes to MB
        }

        // Properties for data binding
        public string DownloadPath { get; set; }
        public int RecheckTimer { get; set; }
        public bool SaveUrlsOnExit { get; set; }
        public bool MinimizeToTray { get; set; }
        public double BandwidthLimiterMB { get; set; } // In MB/s

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

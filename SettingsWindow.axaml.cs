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

            OnPropertyChanged(nameof(DownloadPath));
            OnPropertyChanged(nameof(RecheckTimer));
            OnPropertyChanged(nameof(SaveUrlsOnExit));
            OnPropertyChanged(nameof(MinimizeToTray));
            OnPropertyChanged(nameof(BandwidthLimiterMB));
        }

        // Properties for data binding
        private string _downloadPath;
        public string DownloadPath
        {
            get => _downloadPath;
            set
            {
                if (_downloadPath != value)
                {
                    _downloadPath = value;
                    OnPropertyChanged(nameof(DownloadPath));
                }
            }
        }

        private int _recheckTimer;
        public int RecheckTimer
        {
            get => _recheckTimer;
            set
            {
                if (_recheckTimer != value)
                {
                    _recheckTimer = value;
                    OnPropertyChanged(nameof(RecheckTimer));
                }
            }
        }

        private bool _saveUrlsOnExit;
        public bool SaveUrlsOnExit
        {
            get => _saveUrlsOnExit;
            set
            {
                if (_saveUrlsOnExit != value)
                {
                    _saveUrlsOnExit = value;
                    OnPropertyChanged(nameof(SaveUrlsOnExit));
                }
            }
        }

        private bool _minimizeToTray;
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set
            {
                if (_minimizeToTray != value)
                {
                    _minimizeToTray = value;
                    OnPropertyChanged(nameof(MinimizeToTray));
                }
            }
        }

        private double _bandwidthLimiterMB;
        public double BandwidthLimiterMB
        {
            get => _bandwidthLimiterMB;
            set
            {
                if (_bandwidthLimiterMB != value)
                {
                    _bandwidthLimiterMB = value;
                    OnPropertyChanged(nameof(BandwidthLimiterMB));
                }
            }
        }

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

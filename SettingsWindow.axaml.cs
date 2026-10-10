using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace oChan
{
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private readonly Config _config;
        private readonly Func<Config, Task>? _applySettings;
        private string _downloadPath = string.Empty;
        private decimal? _recheckTimer;
        private decimal? _maxParallelDownloads;
        private decimal? _bandwidthLimiterMB;
        private bool _saveUrlsOnExit;
        private bool _minimizeToTray;
        private bool _isBusy;
        private bool _isClosed;
        private string? _errorMessage;

        public new event PropertyChangedEventHandler? PropertyChanged;

        public SettingsWindow() : this(new Config())
        {
        }

        /// <param name="applySettings">Commits the validated candidate and applies it to the running session. When omitted, the dialog saves the candidate itself.</param>
        public SettingsWindow(Config config, Func<Config, Task>? applySettings = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            InitializeComponent();
            _config = config;
            _applySettings = applySettings;
            DownloadPath = config.DownloadPath;
            RecheckTimer = config.RecheckTimer;
            MaxParallelDownloads = config.MaxParallelDownloads;
            SaveUrlsOnExit = config.SaveUrlsOnExit;
            MinimizeToTray = config.MinimizeToTray;
            BandwidthLimiterMB = config.BandwidthLimiter / (1024m * 1024m);
            DataContext = this;
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set => SetField(ref _downloadPath, value, nameof(DownloadPath));
        }

        public decimal? RecheckTimer
        {
            get => _recheckTimer;
            set => SetField(ref _recheckTimer, value, nameof(RecheckTimer));
        }

        public decimal? MaxParallelDownloads
        {
            get => _maxParallelDownloads;
            set => SetField(ref _maxParallelDownloads, value, nameof(MaxParallelDownloads));
        }

        public decimal? BandwidthLimiterMB
        {
            get => _bandwidthLimiterMB;
            set => SetField(ref _bandwidthLimiterMB, value, nameof(BandwidthLimiterMB));
        }

        public bool SaveUrlsOnExit
        {
            get => _saveUrlsOnExit;
            set => SetField(ref _saveUrlsOnExit, value, nameof(SaveUrlsOnExit));
        }

        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetField(ref _minimizeToTray, value, nameof(MinimizeToTray));
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetField(ref _isBusy, value, nameof(IsBusy));
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                SetField(ref _errorMessage, value, nameof(ErrorMessage));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
                if (HasError && !_isClosed && IsVisible)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_isClosed && IsVisible && SettingsError.IsAttachedToVisualTree())
                        {
                            SettingsError.BringIntoView();
                        }
                    }, DispatcherPriority.Loaded);
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (IsBusy && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            base.OnClosed(e);
        }

        private async void OnBrowseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                if (!StorageProvider.CanPickFolder)
                {
                    ErrorMessage = "Folder selection is unavailable. Enter a download path instead.";
                    return;
                }

                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Download Folder",
                    AllowMultiple = false
                });

                try
                {
                    if (folders.Count > 0)
                    {
                        string? selectedPath = folders[0].TryGetLocalPath();
                        if (string.IsNullOrWhiteSpace(selectedPath))
                        {
                            ErrorMessage = "Choose a folder on the local file system.";
                        }
                        else
                        {
                            DownloadPath = selectedPath;
                        }
                    }
                }
                finally
                {
                    foreach (IStorageFolder folder in folders)
                    {
                        folder.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                ErrorMessage = $"Could not open the folder picker: {exception.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async void OnCloseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                if (DataValidationErrors.GetHasErrors(RecheckTimerInput) ||
                    DataValidationErrors.GetHasErrors(BandwidthInput) ||
                    DataValidationErrors.GetHasErrors(ParallelDownloadsInput))
                {
                    throw new ArgumentException("Check the highlighted settings for invalid numbers.");
                }

                Config candidate = _config.Clone();
                candidate.DownloadPath = DownloadPath;
                candidate.RecheckTimer = GetPositiveInteger(RecheckTimer, "Recheck timer");
                candidate.MaxParallelDownloads = GetPositiveInteger(MaxParallelDownloads, "Parallel downloads");
                candidate.SaveUrlsOnExit = SaveUrlsOnExit;
                candidate.MinimizeToTray = MinimizeToTray;
                if (!BandwidthLimiterMB.HasValue || BandwidthLimiterMB.Value <= 0)
                {
                    throw new ArgumentException("Bandwidth limit must be greater than zero.");
                }

                candidate.BandwidthLimiter = checked((long)(BandwidthLimiterMB.Value * 1024m * 1024m));
                candidate.Validate();
                if (_applySettings != null)
                {
                    await _applySettings(candidate);
                }
                else
                {
                    candidate.SaveConfig();
                }

                _config.CopyFrom(candidate);
                IsBusy = false;
                Close();
            }
            catch (Exception exception)
            {
                ErrorMessage = $"Could not save and apply settings: {exception.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static int GetPositiveInteger(decimal? value, string label)
        {
            if (!value.HasValue || value.Value <= 0 || value.Value > int.MaxValue || decimal.Truncate(value.Value) != value.Value)
            {
                throw new ArgumentException($"{label} must be a positive whole number.");
            }

            return decimal.ToInt32(value.Value);
        }

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Desktop;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace oChan;

public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly ProfileLease _profileLease;
    private readonly Registry _registry;
    private readonly CoalescedUiUpdate _sessionRefresh;
    private readonly WindowNotificationManager _notificationManager;
    private readonly Queue<Notification> _startupNotifications = new Queue<Notification>();
    private readonly Dictionary<IThread, ThreadRow> _threadRows = new Dictionary<IThread, ThreadRow>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IBoard, BoardRow> _boardRows = new Dictionary<IBoard, BoardRow>(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _knownWarnings = new HashSet<string>(StringComparer.Ordinal);
    private readonly Task _restoreTask;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _clipboardMenuItem;
    private bool _trayInteractionObserved;
    private bool _hasOpened;
    private bool _restoreCompleted;
    private bool _isClosing;
    private bool _shutdownRunning;
    private bool _allowClose;
    private bool _importing;

    public ObservableCollection<ThreadRow> UrlList { get; } = new ObservableCollection<ThreadRow>();
    public ObservableCollection<BoardRow> BoardsList { get; } = new ObservableCollection<BoardRow>();

    public MainWindow()
    {
        InitializeComponent();
        string profileDirectory = Config.GetConfigDirectory();
        _profileLease = ProfileLease.Acquire(profileDirectory);
        DownloadQueue? downloadQueue = null;
        try
        {
            _config = Config.LoadConfig(profileDirectory);
            downloadQueue = new DownloadQueue(_config.MaxParallelDownloads, _config.BandwidthLimiter, logger: Log.Logger);
            IImageBoard[] providers = new IImageBoard[] { new FourChanImageBoard(logger: Log.Logger), new EightKunImageBoard(logger: Log.Logger) };
            SessionStore sessionStore = new SessionStore(profileDirectory, _profileLease);
            _registry = new Registry(providers, downloadQueue, Path.GetFullPath(_config.DownloadPath),
                TimeSpan.FromSeconds(_config.RecheckTimer), _config.RecheckTimer, sessionStore, _config.SaveUrlsOnExit, logger: Log.Logger);
            _sessionRefresh = new CoalescedUiUpdate(RefreshSession);
            _registry.StateChanged += OnSessionChanged;
            DataContext = this;

            // The Add button is wired once in XAML. Enter uses that same action.
            this.FindControl<TextBox>("UrlInput")!.KeyDown += OnUrlInputKeyDown;
            this.FindControl<MenuItem>("SettingsMenuItem")!.Click += OnSettingsMenuItemClick;
            this.FindControl<MenuItem>("AboutMenuItem")!.Click += OnAboutMenuItemClick;
            this.FindControl<MenuItem>("ExitMenuItem")!.Click += async (sender, e) => await CloseApplicationAsync();
            this.FindControl<DataGrid>("ThreadsDataGrid")!.AddHandler(PointerPressedEvent,
                OnThreadPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            this.FindControl<DataGrid>("BoardsDataGrid")!.AddHandler(PointerPressedEvent,
                OnBoardPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);

            _notificationManager = new WindowNotificationManager(this)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3
            };
            SetupTrayIcon();
            Opened += OnMainWindowOpened;
            UpdateInteractionState();
            _restoreTask = RestoreSessionAsync();
        }
        catch
        {
            // Construction is passive until RestoreSessionAsync, which observes its own failures.
            downloadQueue?.StopAll();
            DisposeTrayIcon();
            _profileLease.Dispose();
            throw;
        }
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnMainWindowOpened;
        _hasOpened = true;
        if (!string.IsNullOrWhiteSpace(_config.LoadWarning))
        {
            ShowNotification("Settings", _config.LoadWarning, NotificationType.Warning);
        }

        while (_startupNotifications.TryDequeue(out Notification? notification))
        {
            _notificationManager.Show(notification);
        }
    }

    private void OnSessionChanged() => _sessionRefresh.Request();

    private void UpdateInteractionState()
    {
        bool ready = _restoreCompleted && !_isClosing;
        this.FindControl<Button>("AddUrlButton")!.IsEnabled = ready && !_importing;
        this.FindControl<TextBox>("UrlInput")!.IsEnabled = ready;
        this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = ready;
        if (_clipboardMenuItem != null)
        {
            _clipboardMenuItem.IsEnabled = ready && !_importing;
        }
    }

    private void RefreshSession()
    {
        if (_isClosing)
        {
            return;
        }

        RegistryView view = _registry.GetView();
        HashSet<IThread> activeThreads = new HashSet<IThread>(view.Threads.Keys, ReferenceEqualityComparer.Instance);
        HashSet<IBoard> activeBoards = new HashSet<IBoard>(view.Boards.Keys, ReferenceEqualityComparer.Instance);
        foreach (IThread source in _threadRows.Keys.Where(source => !activeThreads.Contains(source)).ToArray())
        {
            ThreadRow row = _threadRows[source];
            _threadRows.Remove(source);
            UrlList.Remove(row);
            row.Dispose();
        }

        foreach (IBoard source in _boardRows.Keys.Where(source => !activeBoards.Contains(source)).ToArray())
        {
            BoardRow row = _boardRows[source];
            _boardRows.Remove(source);
            BoardsList.Remove(row);
            row.Dispose();
        }

        foreach (KeyValuePair<IThread, ThreadSnapshot> entry in view.Threads)
        {
            IThread source = entry.Key;
            if (!_threadRows.TryGetValue(source, out ThreadRow? row))
            {
                row = new ThreadRow(source);
                _threadRows.Add(source, row);
                UrlList.Add(row);
            }

            row.UpdateSnapshot(entry.Value);
        }

        foreach (KeyValuePair<IBoard, BoardSnapshot> entry in view.Boards)
        {
            IBoard source = entry.Key;
            if (!_boardRows.TryGetValue(source, out BoardRow? row))
            {
                row = new BoardRow(source);
                _boardRows.Add(source, row);
                BoardsList.Add(row);
            }

            row.UpdateSnapshot(entry.Value);
        }

        HashSet<string> currentWarnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (string warning in view.Warnings)
        {
            if (currentWarnings.Add(warning) && !_knownWarnings.Contains(warning))
            {
                ShowNotification("Session", warning, NotificationType.Warning);
            }
        }

        _knownWarnings.Clear();
        _knownWarnings.UnionWith(currentWarnings);
    }

    private async Task RestoreSessionAsync()
    {
        try
        {
            if (_config.SaveUrlsOnExit)
            {
                await _registry.RestoreAsync();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshSession();
                _restoreCompleted = true;
                UpdateInteractionState();
            });
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to restore the session.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowNotification("Session could not be restored",
                    "Adding URLs is disabled. Close the application and restart after resolving the session error: " + exception.Message,
                    NotificationType.Error);
                UpdateInteractionState();
            });
        }
    }

    private async Task<AdmissionResult?> AddUrlAsync(string url)
    {
        if (!_restoreCompleted || _isClosing)
        {
            return null;
        }

        try
        {
            AdmissionResult result = await _registry.AddAsync(url);
            switch (result.Status)
            {
                case AdmissionStatus.AlreadyPresent:
                    ShowNotification("Already added", "This thread or board is already being watched.", NotificationType.Information);
                    break;
                case AdmissionStatus.Invalid:
                    ShowNotification("Invalid URL", "Enter a valid thread or board URL.", NotificationType.Warning);
                    break;
                case AdmissionStatus.Unsupported:
                    ShowNotification("Unsupported URL", "No image board supports this URL.", NotificationType.Warning);
                    break;
            }

            return result;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to add URL {Url}", url);
            ShowNotification("Error", "The URL could not be added.", NotificationType.Error);
            return null;
        }
    }

    private async void OnAddUrl(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || _isClosing || _importing)
        {
            return;
        }

        _importing = true;
        UpdateInteractionState();
        TextBox urlInput = this.FindControl<TextBox>("UrlInput")!;
        string enteredText = urlInput.Text ?? string.Empty;
        try
        {
            AdmissionResult? result = await SubmitInputAsync(enteredText);
            if (!string.IsNullOrWhiteSpace(enteredText)
                && (result?.Status is AdmissionStatus.Added or AdmissionStatus.AlreadyPresent)
                && urlInput.Text == enteredText)
            {
                urlInput.Text = string.Empty;
            }
        }
        finally
        {
            _importing = false;
            UpdateInteractionState();
        }
    }

    private async Task<AdmissionResult?> SubmitInputAsync(string? input)
    {
        if (!_restoreCompleted || _isClosing)
        {
            return null;
        }

        try
        {
            AdmissionResult? lastResult = null;
            bool foundUrl = false;
            await AddInput.SubmitAsync(input, ReadClipboardAsync, async url =>
            {
                if (!_isClosing)
                {
                    foundUrl = true;
                    lastResult = await AddUrlAsync(url);
                }
            });

            if (!foundUrl && !_isClosing && string.IsNullOrWhiteSpace(input))
            {
                ShowNotification("Clipboard", "No valid URLs found in the clipboard.", NotificationType.Information);
            }

            return lastResult;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to read URLs from the clipboard.");
            ShowNotification("Error", "The clipboard could not be read.", NotificationType.Error);
            return null;
        }
    }

    private async Task<string?> ReadClipboardAsync()
    {
        if (_isClosing)
        {
            return null;
        }

        if (Clipboard == null)
        {
            throw new InvalidOperationException("The clipboard is not available on this desktop.");
        }

        return await Clipboard.TryGetTextAsync();
    }

    private void OnUrlInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnAddUrl(sender, new RoutedEventArgs());
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        TextBox urlInput = this.FindControl<TextBox>("UrlInput")!;
        KeyModifiers pasteModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == pasteModifier && e.Key == Key.V && !urlInput.IsFocused)
        {
            e.Handled = true;
            await ImportClipboardAsync();
        }
    }

    private async Task ImportClipboardAsync()
    {
        if (!_restoreCompleted || _isClosing || _importing)
        {
            return;
        }

        _importing = true;
        UpdateInteractionState();
        try
        {
            await SubmitInputAsync(null);
        }
        finally
        {
            _importing = false;
            UpdateInteractionState();
        }
    }

    private void SetupTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon();
            if (_trayIcon.NativeMenuExporter == null)
            {
                DisposeTrayIcon();
                Log.Information("Tray menus are unavailable; minimize will keep the window in the taskbar.");
                return;
            }

            ConfigureTrayIcon(_trayIcon);
        }
        catch (Exception exception)
        {
            DisposeTrayIcon();
            Log.Warning(exception, "Could not create the tray icon; minimize will keep the window in the taskbar.");
        }
    }

    private void ConfigureTrayIcon(TrayIcon trayIcon)
    {
        using Stream iconStream = AssetLoader.Open(new Uri("avares://oChan/Assets/ochan-white.png"));
        trayIcon.Icon = new WindowIcon(iconStream);
        trayIcon.ToolTipText = "oChan - Image Downloader";
        NativeMenu menu = new NativeMenu();
        NativeMenuItem openItem = CreateTrayMenuItem("Open", "list");
        openItem.Click += (sender, e) =>
        {
            _trayInteractionObserved = true;
            RestoreWindow();
        };
        menu.Items.Add(openItem);
        NativeMenuItem folderItem = CreateTrayMenuItem("Open Download Folder", "folder");
        folderItem.Click += (sender, e) => OpenDirectory(_config.DownloadPath);
        menu.Items.Add(folderItem);
        _clipboardMenuItem = CreateTrayMenuItem("Add URLs from Clipboard", "clipboard");
        _clipboardMenuItem.Click += async (sender, e) => await ImportClipboardAsync();
        menu.Items.Add(_clipboardMenuItem);
        NativeMenuItem exitItem = CreateTrayMenuItem("Exit", "power");
        exitItem.Click += async (sender, e) => await CloseApplicationAsync();
        menu.Items.Add(exitItem);
        trayIcon.Menu = menu;
        trayIcon.Clicked += (sender, e) =>
        {
            _trayInteractionObserved = true;
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                Hide();
            }
            else
            {
                RestoreWindow();
            }
        };
        trayIcon.IsVisible = true;
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void MinimizeWindow()
    {
        // Avalonia 11 exposes requested visibility, not whether a shell hosts the icon.
        // Only a real tray interaction proves there is a route back to a hidden window.
        if (_trayInteractionObserved && _trayIcon?.IsVisible == true)
        {
            Hide();
        }
        else
        {
            ShowInTaskbar = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void DisposeTrayIcon()
    {
        TrayIcon? trayIcon = _trayIcon;
        _trayIcon = null;
        _clipboardMenuItem = null;
        _trayInteractionObserved = false;
        try
        {
            trayIcon?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not dispose the tray icon.");
        }
    }

    private static NativeMenuItem CreateTrayMenuItem(string header, string icon)
    {
        using Stream iconStream = AssetLoader.Open(new Uri($"avares://oChan/Assets/icons/{icon}.png"));
        return new NativeMenuItem
        {
            Header = header,
            Icon = new Bitmap(iconStream)
        };
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        if (_config.MinimizeToTray && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            MinimizeWindow();
            return;
        }

        await CloseApplicationAsync();
    }

    private async Task CloseApplicationAsync()
    {
        if (_shutdownRunning || _allowClose)
        {
            return;
        }

        _isClosing = true;
        _shutdownRunning = true;
        UpdateInteractionState();
        try
        {
            await _restoreTask;
            await _registry.StopAsync();
            await _registry.DisposeAsync();
            _registry.StateChanged -= OnSessionChanged;
            _sessionRefresh.Dispose();
            foreach (ThreadRow row in _threadRows.Values)
            {
                row.Dispose();
            }

            foreach (BoardRow row in _boardRows.Values)
            {
                row.Dispose();
            }

            // Detach menu subscriptions before flushing queued DBus layout updates.
            // Avalonia 11 does not cancel those Background callbacks on Dispose.
            _clipboardMenuItem = null;
            if (_trayIcon != null)
            {
                _trayIcon.Menu = null;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            DisposeTrayIcon();
            _profileLease.Dispose();
            _allowClose = true;
            Close();
            Log.CloseAndFlush();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to stop the application.");
            ShowNotification("Error", "The application could not finish stopping. Try Exit again.", NotificationType.Error);
        }
        finally
        {
            _shutdownRunning = false;
        }
    }

    private void ShowNotification(string title, string message, NotificationType type)
    {
        if (_allowClose)
        {
            return;
        }

        Notification notification = new Notification(title, message, type);
        if (!_hasOpened)
        {
            _startupNotifications.Enqueue(notification);
        }
        else
        {
            _notificationManager.Show(notification);
        }
    }

    private IThread? SelectedThread => (this.FindControl<DataGrid>("ThreadsDataGrid")?.SelectedItem as ThreadRow)?.Source;
    private IBoard? SelectedBoard => (this.FindControl<DataGrid>("BoardsDataGrid")?.SelectedItem as BoardRow)?.Source;

    private void OnOpenThreadInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedThread is IThread thread)
        {
            OpenTarget(thread.ThreadUri.ToString());
        }
    }

    private void OnOpenBoardInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedBoard is IBoard board)
        {
            OpenTarget(board.BoardUri.ToString());
        }
    }

    private void OpenTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to open {Target}", target);
            ShowNotification("Error", "The selected location could not be opened.", NotificationType.Error);
        }
    }

    private void OpenDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            OpenTarget(directory);
        }
        else
        {
            ShowNotification("Directory not found", "The download directory does not exist yet.", NotificationType.Warning);
        }
    }

    private void OnOpenThreadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing && SelectedThread is IThread thread)
        {
            OpenDirectory(thread.DownloadDirectory);
        }
    }

    private void OnOpenBoardDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing && this.FindControl<DataGrid>("BoardsDataGrid")?.SelectedItem is BoardRow row)
        {
            OpenDirectory(row.DownloadDirectory);
        }
    }

    private async void OnRemoveThreadMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || _isClosing || SelectedThread is not IThread thread)
        {
            return;
        }

        try
        {
            await _registry.RemoveThreadAsync(thread);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to remove thread {ThreadUri}", thread.ThreadUri);
            ShowNotification("Error", "The thread could not be stopped.", NotificationType.Error);
        }
    }

    private async void OnRemoveBoardMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || _isClosing || SelectedBoard is not IBoard board)
        {
            return;
        }

        try
        {
            await _registry.RemoveBoardAsync(board);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to remove board {BoardUri}", board.BoardUri);
            ShowNotification("Error", "The board could not be stopped.", NotificationType.Error);
        }
    }

    private static void SelectContextRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is DataGrid grid && e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
        {
            Visual? hit = grid.InputHitTest(e.GetPosition(grid)) as Visual;
            DataGridRow? row = hit as DataGridRow ?? hit?.FindAncestorOfType<DataGridRow>();
            grid.SelectedItem = row?.DataContext;
        }
    }

    private void OnThreadPointerPressed(object? sender, PointerPressedEventArgs e) => SelectContextRow(sender, e);
    private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e) => SelectContextRow(sender, e);
    private void OnThreadDoubleTapped(object? sender, TappedEventArgs e) => OnOpenThreadDirectoryClick(sender, e);
    private void OnBoardDoubleTapped(object? sender, TappedEventArgs e) => OnOpenBoardDirectoryClick(sender, e);

    private async void OnSettingsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (_restoreCompleted && !_isClosing)
        {
            SettingsWindow settingsWindow = new SettingsWindow(_config, ApplySettingsAsync);
            await settingsWindow.ShowDialog(this);
        }
    }

    private Task ApplySettingsAsync(Config candidate)
    {
        if (!_restoreCompleted || _isClosing)
        {
            throw new InvalidOperationException("The session is not ready to apply settings.");
        }

        SessionOptions options = new SessionOptions
        {
            DownloadPath = candidate.DownloadPath,
            RecheckInterval = TimeSpan.FromSeconds(candidate.RecheckTimer),
            MaxParallelDownloads = candidate.MaxParallelDownloads,
            BandwidthBytesPerSecond = candidate.BandwidthLimiter
        };
        return _registry.ApplyOptionsAsync(options, candidate.SaveUrlsOnExit, candidate.SaveConfig);
    }

    private async void OnAboutMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing)
        {
            AboutWindow aboutWindow = new AboutWindow();
            await aboutWindow.ShowDialog(this);
        }
    }
}

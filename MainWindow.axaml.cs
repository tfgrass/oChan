using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.IO;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;
using System;
using System.Linq;
using Avalonia.Controls.Notifications;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using System.Text.Json;

using System.Collections.Generic;
using System.Threading.Tasks;

namespace oChan
{
    // Define the SavedUrls class within the namespace
    public class SavedUrls
    {
        public List<string> ThreadUrls { get; set; } = new List<string>();
        public List<string> BoardUrls { get; set; } = new List<string>();
    }


    public partial class MainWindow : Window
    {
        public ObservableCollection<IThread> UrlList { get; set; }
        public ObservableCollection<IBoard> BoardsList { get; set; }

        private Registry _Registry;
        private DownloadQueue sharedDownloadQueue;
        private WindowNotificationManager notificationManager;

        private const string SavedUrlsFileName = "saved_urls.json";

        public MainWindow()
        {
            InitializeComponent();

            // Initialize the registry
            _Registry = new Registry();

            // Initialize the UrlList for the DataGrid
            UrlList = new ObservableCollection<IThread>();

            // Initialize the BoardsList for the DataGrid
            BoardsList = new ObservableCollection<IBoard>();

            // Initialize shared download queue
            sharedDownloadQueue = new DownloadQueue(5, 1024 * 1024 * 10); // Adjust as needed

            // Set the DataContext to the window itself
            this.DataContext = this;

            // Wire up event handlers for buttons and menu items
            var addUrlButton = this.FindControl<Button>("AddUrlButton");
            addUrlButton.Click += OnAddUrl;

            // Wire up the Enter key event handler for the TextBox
            var urlInput = this.FindControl<TextBox>("UrlInput");
            urlInput.KeyDown += OnUrlInputKeyDown;

            var settingsMenuItem = this.FindControl<MenuItem>("SettingsMenuItem");
            settingsMenuItem.Click += OnSettingsMenuItemClick;

            var aboutMenuItem = this.FindControl<MenuItem>("AboutMenuItem");
            aboutMenuItem.Click += OnAboutMenuItemClick;

            // Handle clipboard paste (CTRL+V or equivalent)
            this.AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);

            // Initialize the notification manager
            notificationManager = new WindowNotificationManager(this)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3
            };

            // Initialize the tray icon
            SetupTrayIcon();

            // Load saved URLs if the setting is enabled
            var config = _Registry.GetConfig();
            if (config.SaveUrlsOnExit)
            {
                // Fire and forget the async loading
                _ = LoadUrls();
            }
        }

        /// <summary>
        /// Saves the current thread and board URLs to a file.
        /// </summary>
        private void SaveUrls()
        {
            try
            {
                var savedUrls = new SavedUrls
                {
                    ThreadUrls = UrlList.Select(t => t.ThreadUri.ToString()).ToList(),
                    BoardUrls = BoardsList.Select(b => b.BoardUri.ToString()).ToList()
                };

                string configDir = Config.GetConfigDirectory();
                string filePath = Path.Combine(configDir, SavedUrlsFileName);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = ConfigJsonContext.Default
                };

                string json = JsonSerializer.Serialize(savedUrls, options);
                File.WriteAllText(filePath, json);

                Log.Information("Successfully saved URLs to {FilePath}", filePath);
                notificationManager.Show(new Notification("Success", "URLs have been saved successfully.", NotificationType.Success));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save URLs on exit.");
                notificationManager.Show(new Notification("Error", "Failed to save URLs on exit.", NotificationType.Error));
            }
        }

        /// <summary>
        /// Loads thread and board URLs from a file.
        /// </summary>
        private async Task LoadUrls()
        {
            try
            {
                string configDir = Config.GetConfigDirectory();
                string filePath = Path.Combine(configDir, SavedUrlsFileName);

                if (!File.Exists(filePath))
                {
                    Log.Warning("Saved URLs file not found at {FilePath}.", filePath);
                    return;
                }

                var options = new JsonSerializerOptions
                {
                    TypeInfoResolver = ConfigJsonContext.Default
                };

                string json = await File.ReadAllTextAsync(filePath);
                var savedUrls = JsonSerializer.Deserialize<SavedUrls>(json, options);

                if (savedUrls == null)
                {
                    Log.Warning("No URLs found in the saved URLs file.");
                    return;
                }

                // Load Thread URLs
                foreach (var threadUrl in savedUrls.ThreadUrls)
                {
                    await AddUrl(threadUrl);
                }

                // Load Board URLs
                foreach (var boardUrl in savedUrls.BoardUrls)
                {
                    await AddUrl(boardUrl);
                }

                Log.Information("Successfully loaded URLs from {FilePath}", filePath);
                notificationManager.Show(new Notification("Success", "URLs have been loaded successfully.", NotificationType.Success));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load saved URLs on startup.");
                notificationManager.Show(new Notification("Error", "Failed to load saved URLs on startup.", NotificationType.Error));
            }
        }

        // Method to open the download folder
        private void OpenDownloadFolder()
        {
            try
            {
                string downloadFolder = _Registry.GetConfig().DownloadPath;
                if (Directory.Exists(downloadFolder))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = downloadFolder,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                else
                {
                    Log.Warning("Download folder not found: {DownloadPath}", downloadFolder);
                    notificationManager.Show(new Notification("Error", "Download folder not found.", NotificationType.Error));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening download folder.");
                notificationManager.Show(new Notification("Error", "Failed to open download folder.", NotificationType.Error));
            }
        }

        // Method to set up the tray icon
        private void SetupTrayIcon()
        {
            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://oChan/Assets/ochan-white.png"))),
                ToolTipText = "oChan - Image Downloader"
            };

            // Context menu for the tray icon
            var menu = new NativeMenu();

            // Create "Open" menu item with icon
            var openMenuItem = new NativeMenuItem { Header = "Open" };
            openMenuItem.Icon = new Bitmap(AssetLoader.Open(new Uri("avares://oChan/Assets/icons/list.png")));
            openMenuItem.Click += (s, e) => Show();
            menu.Items.Add(openMenuItem);

            // Create "Open Download Folder" menu item with icon
            var openDownloadFolderMenuItem = new NativeMenuItem { Header = "Open Download Folder" };
            openDownloadFolderMenuItem.Icon = new Bitmap(AssetLoader.Open(new Uri("avares://oChan/Assets/icons/folder.png")));
            openDownloadFolderMenuItem.Click += (s, e) => OpenDownloadFolder();
            menu.Items.Add(openDownloadFolderMenuItem);

            // Create "Add URLs from Clipboard" menu item with icon
            var addUrlsFromClipboardMenuItem = new NativeMenuItem { Header = "Add URLs from Clipboard" };
            addUrlsFromClipboardMenuItem.Icon = new Bitmap(AssetLoader.Open(new Uri("avares://oChan/Assets/icons/clipboard.png")));
            addUrlsFromClipboardMenuItem.Click += async (s, e) => await AddUrlsFromClipboard();
            menu.Items.Add(addUrlsFromClipboardMenuItem);

            // Create "Exit" menu item with icon
            var exitMenuItem = new NativeMenuItem { Header = "Exit" };
            exitMenuItem.Icon = new Bitmap(AssetLoader.Open(new Uri("avares://oChan/Assets/icons/power.png")));
            exitMenuItem.Click += (s, e) => CloseApplication();
            menu.Items.Add(exitMenuItem);

            trayIcon.Menu = menu;

            trayIcon.Clicked += (s, e) =>
            {
                if (this.IsVisible)
                {
                    Hide();
                }
                else
                {
                    Show();
                }
            };

            trayIcon.IsVisible = true;
        }

        // Handle closing the application gracefully
        private void CloseApplication()
        {
            // Perform cleanup and notify the user (optional)
            notificationManager.Show(new Avalonia.Controls.Notifications.Notification("Exiting", "The application will close now.", NotificationType.Information));

            // Save URLs if the setting is enabled
            var config = _Registry.GetConfig();
            if (config.SaveUrlsOnExit)
            {
                SaveUrls();
            }

            // Make sure to explicitly exit the application
            Dispatcher.UIThread.Post(() =>
            {
                Environment.Exit(0);  // Exit the application entirely
            });
        }

        // Handle window closing (minimize to tray)
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            // Check the config for minimizing to tray
            var config = _Registry.GetConfig();

            if (config.MinimizeToTray)
            {
                // Minimize to tray on close instead of exiting
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    e.Cancel = true;  // Cancel window close
                    Hide();           // Hide window to minimize to tray
                }
            }
            else
            {
                // Save URLs on exit if configured to do so
                if (config.SaveUrlsOnExit)
                {
                    SaveUrls();
                }

                // Proceed with application exit
                CloseApplication();
            }
        }

        // Handle Enter key in the TextBox to trigger AddUrl functionality
        private void OnUrlInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Log.Information("Enter pressed in the TextBox, triggering Add URL.");
                var addUrlButton = this.FindControl<Button>("AddUrlButton");
                addUrlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        // Event handler for the "Add" button
        private async void OnAddUrl(object sender, RoutedEventArgs e)
        {
            var urlInput = this.FindControl<TextBox>("UrlInput");
            string url = urlInput.Text ?? string.Empty;

            if (string.IsNullOrEmpty(url))
            {
                await AddUrlsFromClipboard();
            }
            else
            {
                await AddUrl(url);
                urlInput.Text = string.Empty;
            }
        }

        // Method to add a single URL
        private async Task AddUrl(string url)
        {
            try
            {
                IThread? thread = _Registry.GetThread(url);
                if (thread != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        UrlList.Add(thread);
                    });

                    thread.ThreadRemoved += (thread) => OnThreadRemoved(thread, false);

                    ArchiveOptions options = new ArchiveOptions
                    {
                        DownloadQueue = sharedDownloadQueue
                    };
                    await thread.ArchiveAsync(options);
                }
                else
                {
                    IBoard? board = _Registry.GetBoard(url);
                    if (board != null)
                    {
                        board.ThreadDiscovered += OnThreadDiscovered;
                        board.StartMonitoring(_Registry.GetConfig().RecheckTimer);

                        Dispatcher.UIThread.Post(() =>
                        {
                            BoardsList.Add(board);
                        });
                    }
                    else
                    {
                        Log.Warning("No image board could handle the URL: {Url}", url);
                        notificationManager.Show(new Notification("Warning", "No image board could handle the provided URL.", NotificationType.Warning));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred while handling the URL: {Url}", url);
                notificationManager.Show(new Notification("Error", $"An error occurred: {ex.Message}", NotificationType.Error));
            }
        }

        // Method to discover and add URLs from the clipboard
        private async Task AddUrlsFromClipboard()
        {
            var clipboard = this.Clipboard; // Access clipboard using the TopLevel instance
            var clipboardText = await clipboard.GetTextAsync();

            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                var urls = DiscoverUrls(clipboardText);
                if (urls.Length == 0)
                {
                    Log.Information("No valid URLs found in the clipboard.");
                    notificationManager.Show(new Notification("Info", "No valid URLs found in the clipboard.", NotificationType.Information));
                    return;
                }

                foreach (var url in urls)
                {
                    await AddUrl(url);
                }
            }
            else
            {
                Log.Information("No URLs found in the clipboard.");
                notificationManager.Show(new Notification("Info", "Clipboard is empty or does not contain URLs.", NotificationType.Information));
            }
        }

        // Utility method to discover URLs from a given string
        private string[] DiscoverUrls(string text)
        {
            var urls = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(line => Uri.IsWellFormedUriString(line.Trim(), UriKind.Absolute))
                           .Select(line => line.Trim())
                           .ToArray();

            Log.Information("Discovered {Count} URLs from the clipboard.", urls.Length);
            return urls;
        }

        // Handle clipboard paste (CTRL+V)
        private async void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // Get the UrlInput TextBox
            var urlInput = this.FindControl<TextBox>("UrlInput");

            // Check if CTRL+V is pressed and the TextBox is NOT focused
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V && !urlInput.IsFocused)
            {
                Log.Information("CTRL+V pressed, attempting to paste URLs from clipboard.");

                // Add URLs from clipboard
                await AddUrlsFromClipboard();
                urlInput.Text = string.Empty;
            }
        }

        // Handle thread removal
        private void OnThreadRemoved(IThread thread, bool abort)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.Information("Removing thread {ThreadId} from the UI.", thread.ThreadId);
                if (abort)
                {
                    sharedDownloadQueue.CancelDownloadsForThread(thread); // Cancel all downloads for this thread
                }

                UrlList.Remove(thread);
                _Registry.RemoveThread(thread.ThreadUri.ToString());

                // Optionally notify the user
                notificationManager.Show(new Notification("Thread Removed", $"Thread {thread.NiceName} has been removed.", NotificationType.Warning));
            });
        }

        // Handle board removal
        private void OnBoardRemoved(IBoard board)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.Information("Removing board {BoardUri} from the UI.", board.BoardUri);
                board.StopMonitoring();
                BoardsList.Remove(board);
                _Registry.RemoveBoard(board.BoardUri.ToString());

                // Optionally notify the user
                notificationManager.Show(new Notification("Board Removed", $"Board {board.NiceName} has been removed.", NotificationType.Warning));
            });
        }

        // Handle thread directory opening
        private void OpenThreadDirectory(IThread thread)
        {
            string directoryPath = Path.Combine(_Registry.GetConfig().DownloadPath, thread.Board.BoardCode, thread.ThreadId);
            if (Directory.Exists(directoryPath))
            {
                OpenDirectory(directoryPath);
            }
            else
            {
                Log.Warning("Directory not found for thread {ThreadId}: {DirectoryPath}", thread.ThreadId, directoryPath);
                notificationManager.Show(new Notification("Error", "Thread download directory not found.", NotificationType.Error));
            }
        }

        // Handle board directory opening
        private void OpenBoardDirectory(IBoard board)
        {
            string directoryPath = Path.Combine(_Registry.GetConfig().DownloadPath, board.BoardCode);
            if (Directory.Exists(directoryPath))
            {
                OpenDirectory(directoryPath);
            }
            else
            {
                Log.Warning("Directory not found for board {BoardCode}: {DirectoryPath}", board.BoardCode, directoryPath);
                notificationManager.Show(new Notification("Error", "Board download directory not found.", NotificationType.Error));
            }
        }

        // Utility method to open directory
        private void OpenDirectory(string directoryPath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directoryPath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening directory: {DirectoryPath}", directoryPath);
                notificationManager.Show(new Notification("Error", "Failed to open directory.", NotificationType.Error));
            }
        }

        // Event handler for the "Open Directory" context menu click for threads
        private void OnOpenThreadDirectoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                var dataGrid = this.FindControl<DataGrid>("ThreadsDataGrid");
                if (dataGrid != null && dataGrid.SelectedItem is IThread thread)
                {
                    OpenThreadDirectory(thread);
                }
            }
        }

        // Event handler for the "Open Directory" context menu click for boards
        private void OnOpenBoardDirectoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                var dataGrid = this.FindControl<DataGrid>("BoardsDataGrid");
                if (dataGrid != null && dataGrid.SelectedItem is IBoard board)
                {
                    OpenBoardDirectory(board);
                }
            }
        }

        private void OnThreadDiscovered(object sender, ThreadEventArgs e)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var thread = e.Thread;
                UrlList.Add(thread);
                thread.ThreadRemoved += (thread) => OnThreadRemoved(thread, false);

                ArchiveOptions options = new ArchiveOptions
                {
                    DownloadQueue = sharedDownloadQueue
                };
                await thread.ArchiveAsync(options);

                // Optionally notify the user
                notificationManager.Show(new Notification("New Thread", $"Thread {thread.NiceName} has been added.", NotificationType.Information));
            });
        }

        // Method to remove a thread based on ThreadUrl
        private void RemoveThread(IThread thread)
        {
            if (thread != null)
            {
                Log.Information("Manually removing thread with URL: {ThreadUri}", thread.ThreadUri);
                Dispatcher.UIThread.Post(() =>
                {
                    var threadToRemove = UrlList.FirstOrDefault(t => t.ThreadUri == thread.ThreadUri);
                    if (threadToRemove != null)
                    {
                        UrlList.Remove(threadToRemove);
                        threadToRemove.NotifyThreadRemoval(true);

                        _Registry.RemoveThread(thread.ThreadUri.ToString());
                        Log.Information("Thread with URL {ThreadUri} removed successfully.", thread.ThreadUri);

                        // Optionally notify the user
                        notificationManager.Show(new Notification("Thread Removed", $"Thread {thread.NiceName} has been removed.", NotificationType.Information));
                    }
                    else
                    {
                        Log.Warning("Thread with URL {ThreadUri} not found in UrlList.", thread.ThreadUri);
                        notificationManager.Show(new Notification("Warning", "Thread not found in the list.", NotificationType.Warning));
                    }
                });
            }
        }

        // Method to remove a board
        private void RemoveBoard(IBoard board)
        {
            if (board != null)
            {
                Log.Information("Manually removing board with URL: {BoardUri}", board.BoardUri);
                OnBoardRemoved(board);

                // Optionally notify the user
                notificationManager.Show(new Notification("Board Removed", $"Board {board.NiceName} has been removed.", NotificationType.Information));
            }
        }

        // Event handler for the "Remove Thread" MenuItem click
        private void OnRemoveThreadMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                var dataGrid = this.FindControl<DataGrid>("ThreadsDataGrid");
                if (dataGrid != null && dataGrid.SelectedItem is IThread thread)
                {
                    RemoveThread(thread);
                }
                else
                {
                    Log.Error("DataGrid SelectedItem is not an IThread instance.");
                    notificationManager.Show(new Notification("Error", "Selected item is not a valid thread.", NotificationType.Error));
                }
            }
            else
            {
                Log.Error("Sender is not a MenuItem.");
                notificationManager.Show(new Notification("Error", "Invalid menu item.", NotificationType.Error));
            }
        }

        // Event handler for the "Remove Board" MenuItem click
        private void OnRemoveBoardMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                var dataGrid = this.FindControl<DataGrid>("BoardsDataGrid");
                if (dataGrid != null && dataGrid.SelectedItem is IBoard board)
                {
                    RemoveBoard(board);
                }
                else
                {
                    Log.Error("DataGrid SelectedItem is not an IBoard instance.");
                    notificationManager.Show(new Notification("Error", "Selected item is not a valid board.", NotificationType.Error));
                }
            }
            else
            {
                Log.Error("Sender is not a MenuItem.");
                notificationManager.Show(new Notification("Error", "Invalid menu item.", NotificationType.Error));
            }
        }

        // Event handler to select thread row on right-click
        private void OnDataGridPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is DataGrid dataGrid)
            {
                var pointerPoint = e.GetCurrentPoint(dataGrid);
                if (pointerPoint.Properties.IsRightButtonPressed)
                {
                    var point = e.GetPosition(dataGrid);
                    var hitTestResult = dataGrid.InputHitTest(point);
                    if (hitTestResult is DataGridRow row)
                    {
                        dataGrid.SelectedItem = row.DataContext;
                    }
                }
            }
        }

        // Event handler to select board row on right-click
        private void OnBoardDataGridPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is DataGrid dataGrid)
            {
                var pointerPoint = e.GetCurrentPoint(dataGrid);
                if (pointerPoint.Properties.IsRightButtonPressed)
                {
                    var point = e.GetPosition(dataGrid);
                    var hitTestResult = dataGrid.InputHitTest(point);
                    if (hitTestResult is DataGridRow row)
                    {
                        dataGrid.SelectedItem = row.DataContext;
                    }
                }
            }
        }

        private void OnSettingsMenuItemClick(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_Registry.GetConfig());
            settingsWindow.ShowDialog(this);
        }

        private void OnAboutMenuItemClick(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog(this);
        }

        private void OnThreadPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Add logic for pointer pressed
        }

        private void OnThreadDoubleTapped(object? sender, TappedEventArgs e)
        {
            Log.Debug("Double tapped on a thread.");
            var dataGrid = this.FindControl<DataGrid>("ThreadsDataGrid");
            if (dataGrid != null && dataGrid.SelectedItem is IThread thread)
            {
                OpenThreadDirectory(thread);
            }
        }

        private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Add logic for pointer pressed on board
        }

        private void OnBoardDoubleTapped(object? sender, TappedEventArgs e)
        {
            Log.Debug("Double tapped on a board.");
            var dataGrid = this.FindControl<DataGrid>("BoardsDataGrid");
            if (dataGrid != null && dataGrid.SelectedItem is IBoard board)
            {
                OpenBoardDirectory(board);
            }
        }
    }
}

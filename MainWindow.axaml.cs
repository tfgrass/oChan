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
using Avalonia.Input.Platform;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Media.Imaging;

namespace oChan
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<IThread> UrlList { get; set; }
        public ObservableCollection<IBoard> BoardsList { get; set; }

        private Registry _Registry;
        private DownloadQueue sharedDownloadQueue;
        private WindowNotificationManager notificationManager;

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
        }

        // Method to open the download folder
        private void OpenDownloadFolder()
        {
            try
            {
                string downloadFolder = new Config().DownloadPath;
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
            }
        }

        // Method to set up the tray icon
        private void SetupTrayIcon()
        {
            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://oChan/Assets/ochan.png"))),
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

            // Minimize to tray on close instead of exiting (optional)
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                e.Cancel = true;  // Cancel window close
                Hide();           // Hide window to minimize to tray
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
        private async System.Threading.Tasks.Task AddUrl(string url)
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
                        board.StartMonitoring(60);

                        Dispatcher.UIThread.Post(() =>
                        {
                            BoardsList.Add(board);
                        });
                    }
                    else
                    {
                        Log.Warning("No image board could handle the URL: {Url}", url);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred while handling the URL: {Url}", url);
            }
        }

        // Method to discover and add URLs from the clipboard
        private async System.Threading.Tasks.Task AddUrlsFromClipboard()
        {
            var clipboard = this.Clipboard; // Access clipboard using the TopLevel instance
            var clipboardText = await clipboard.GetTextAsync();

            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                var urls = DiscoverUrls(clipboardText);
                foreach (var url in urls)
                {
                    await AddUrl(url);
                }
            }
            else
            {
                Log.Information("No URLs found in the clipboard.");
            }
        }

        // Utility method to discover URLs from a given string
        private string[] DiscoverUrls(string text)
        {
            var urls = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(line => Uri.IsWellFormedUriString(line, UriKind.Absolute))
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
            });
        }

        // Handle thread directory opening
        private void OpenThreadDirectory(IThread thread)
        {
            string directoryPath = Path.Combine("Downloads", thread.Board.BoardCode, thread.ThreadId);
            if (Directory.Exists(directoryPath))
            {
                OpenDirectory(directoryPath);
            }
            else
            {
                Log.Warning("Directory not found for thread {ThreadId}: {DirectoryPath}", thread.ThreadId, directoryPath);
            }
        }

        // Handle board directory opening
        private void OpenBoardDirectory(IBoard board)
        {
            string directoryPath = Path.Combine("Downloads", board.BoardCode);
            if (Directory.Exists(directoryPath))
            {
                OpenDirectory(directoryPath);
            }
            else
            {
                Log.Warning("Directory not found for board {BoardCode}: {DirectoryPath}", board.BoardCode, directoryPath);
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
                    }
                    else
                    {
                        Log.Warning("Thread with URL {ThreadUri} not found in UrlList.", thread.ThreadUri);
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
                }
            }
            else
            {
                Log.Error("Sender is not a MenuItem.");
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
                }
            }
            else
            {
                Log.Error("Sender is not a MenuItem.");
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
            Log.Debug("Double tapped on a thread.");
            var dataGrid = this.FindControl<DataGrid>("BoardsDataGrid");
            if (dataGrid != null && dataGrid.SelectedItem is IBoard board)
            {
                OpenBoardDirectory(board);
            }
        }
    }
}

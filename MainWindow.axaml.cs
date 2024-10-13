using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Linq; // For LINQ methods
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;
using System;

namespace oChan
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<IThread> UrlList { get; set; }
        public ObservableCollection<IBoard> BoardsList { get; set; }

        private Registry _Registry;
        private DownloadQueue sharedDownloadQueue;

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

            var settingsMenuItem = this.FindControl<MenuItem>("SettingsMenuItem");
            settingsMenuItem.Click += OnSettingsMenuItemClick;

            var aboutMenuItem = this.FindControl<MenuItem>("AboutMenuItem");
            aboutMenuItem.Click += OnAboutMenuItemClick;
        }

        // Event handler for the "Add" button
        private async void OnAddUrl(object sender, RoutedEventArgs e)
        {
            var urlInput = this.FindControl<TextBox>("UrlInput");
            string url = urlInput.Text ?? string.Empty;

            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    IThread? thread = _Registry.GetThread(url);
                    if (thread != null)
                    {
                        // Add to UrlList on the UI thread
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

                            // Add to BoardsList on the UI thread
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

                urlInput.Text = string.Empty;
            }
        }

        // Event handler for the "Settings" menu item
        private void OnSettingsMenuItemClick(object sender, RoutedEventArgs e)
        {
            // Assuming you have SettingsWindow implemented
            var settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog(this);
        }

        // Event handler for the "About" menu item
        private void OnAboutMenuItemClick(object sender, RoutedEventArgs e)
        {
            // Assuming you have AboutWindow implemented
            var aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog(this);
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
            });
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
                    // Search for the thread in UrlList based on ThreadUrl
                    var threadToRemove = UrlList.FirstOrDefault(t => t.ThreadUri == thread.ThreadUri);
                    if (threadToRemove != null)
                    {
                        UrlList.Remove(threadToRemove);
                        threadToRemove.NotifyThreadRemoval(true); // Pass true to indicate manual removal
                        Log.Information("Thread with URL {ThreadUri} removed successfully.", thread.ThreadUri);
                    }
                    else
                    {
                        Log.Warning("Thread with URL {ThreadUri} not found in UrlList.", thread.ThreadUri);
                    }
                });
            }
        }

        // Event handler for the "Remove" MenuItem click
        private void OnRemoveThreadMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                var dataGrid = this.FindControl<DataGrid>("ThreadsDataGrid");
                if (dataGrid != null)
                {
                    if (dataGrid.SelectedItem is IThread thread)
                    {
                        RemoveThread(thread); // Pass the thread to remove it
                    }
                    else
                    {
                        Log.Error("DataGrid SelectedItem is not an IThread instance.");
                    }
                }
                else
                {
                    Log.Error("ThreadsDataGrid not found.");
                }
            }
            else
            {
                Log.Error("Sender is not a MenuItem.");
            }
        }

        // Event handler to select row on right-click
        private void OnDataGridPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is DataGrid dataGrid)
            {
                // Get the current pointer point relative to the DataGrid
                var pointerPoint = e.GetCurrentPoint(dataGrid);

                // Check if the right mouse button is pressed
                if (pointerPoint.Properties.IsRightButtonPressed)
                {
                    var point = e.GetPosition(dataGrid);

                    // Perform hit testing to determine which element was clicked
                    var hitTestResult = dataGrid.InputHitTest(point);
                    if (hitTestResult is DataGridRow row)
                    {
                        // Select the row that was right-clicked
                        dataGrid.SelectedItem = row.DataContext;
                    }
                }
            }
        }
    }
}

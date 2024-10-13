// MainWindow.axaml.cs

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;
using Avalonia.Threading;

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

            // Set the DataContext for data binding
            this.DataContext = this;

            // Wire up event handlers
            var addUrlButton = this.FindControl<Button>("AddUrlButton");
            addUrlButton.Click += OnAddUrl;

            // Wire up menu item event handlers
            var settingsMenuItem = this.FindControl<MenuItem>("SettingsMenuItem");
            settingsMenuItem.Click += OnSettingsMenuItemClick;

            var aboutMenuItem = this.FindControl<MenuItem>("AboutMenuItem");
            aboutMenuItem.Click += OnAboutMenuItemClick;
        }

        // Event handler for Add button
        private async void OnAddUrl(object sender, RoutedEventArgs e)
        {
            // Get the URL from the input box
            var urlInput = this.FindControl<TextBox>("UrlInput");
            string url = urlInput.Text ?? string.Empty;

            // Check if the URL is not empty
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    // First, try to get a thread
                    IThread? thread = _Registry.GetThread(url);
                    if (thread != null)
                    {
                        // Add the thread to the list for display in the DataGrid
                        UrlList.Add(thread);

                        // Initialize the ArchiveOptions with the shared DownloadQueue
                        ArchiveOptions options = new ArchiveOptions
                        {
                            DownloadQueue = sharedDownloadQueue
                        };

                        // Start the archive process asynchronously
                        await thread.ArchiveAsync(options);
                    }
                    else
                    {
                        // If not a thread, try to get a board
                        IBoard? board = _Registry.GetBoard(url);
                        if (board != null)
                        {
                            // Subscribe to the ThreadDiscovered event
                            board.ThreadDiscovered += OnThreadDiscovered;

                            // Start monitoring the board every 60 seconds
                            board.StartMonitoring(60);

                            // Add the board to the list for display in the DataGrid
                            BoardsList.Add(board);
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
                    // Display an error message to the user if necessary
                }

                // Clear the input box after adding the URL
                urlInput.Text = string.Empty;
            }
        }

        // Event handler for when a new thread is discovered
        private void OnThreadDiscovered(object sender, ThreadEventArgs e)
        {
            // Ensure that updates to UrlList are made on the UI thread
            Dispatcher.UIThread.Post(async () =>
            {
                // Add the new thread to your threads list
                UrlList.Add(e.Thread);

                // Start archiving the thread
                ArchiveOptions options = new ArchiveOptions
                {
                    DownloadQueue = sharedDownloadQueue
                };
                await e.Thread.ArchiveAsync(options);
            });
        }

        // Event handler for Settings menu item
        private void OnSettingsMenuItemClick(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog(this);
        }

        // Event handler for About menu item
        private void OnAboutMenuItemClick(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog(this);
        }

    }
}

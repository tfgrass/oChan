using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Windows.Input;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;

namespace oChan
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<IThread> UrlList { get; set; }
        public ObservableCollection<IBoard> BoardsList { get; set; }
        public ICommand RemoveThreadCommand { get; }

        private Registry _Registry;
        private DownloadQueue sharedDownloadQueue;

        public MainWindow()
        {


            // Initialize the registry
            _Registry = new Registry();

            // Initialize the UrlList for the DataGrid
            UrlList = new ObservableCollection<IThread>();

            // Initialize the BoardsList for the DataGrid
            BoardsList = new ObservableCollection<IBoard>();

            // Initialize shared download queue
            sharedDownloadQueue = new DownloadQueue(5, 1024 * 1024 * 10); // Adjust as needed

            // Define the RemoveThreadCommand and bind the method to it
            RemoveThreadCommand = new RelayCommand<IThread>(RemoveThread);

                        InitializeComponent();

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

                        thread.ThreadRemoved += OnThreadRemoved;

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
        private void OnThreadRemoved(IThread thread)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.Information("Removing thread {ThreadId} from the UI.", thread.ThreadId);
                UrlList.Remove(thread);
            });
        }

        private void OnThreadDiscovered(object sender, ThreadEventArgs e)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var thread = e.Thread;
                UrlList.Add(thread);
                thread.ThreadRemoved += OnThreadRemoved;

                ArchiveOptions options = new ArchiveOptions
                {
                    DownloadQueue = sharedDownloadQueue
                };
                await thread.ArchiveAsync(options);
            });
        }

        // Method to remove a thread
        private void RemoveThread(IThread thread)
        {
            if (thread != null)
            {
                Log.Information("Manually removing thread {ThreadId} from UI", thread.ThreadId);
                Dispatcher.UIThread.Post(() =>
                {
                    UrlList.Remove(thread);
                });
            }
        }
    }

    // Simple relay command implementation
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool>? _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute((T)parameter!);

        public void Execute(object? parameter) => _execute((T)parameter!);

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

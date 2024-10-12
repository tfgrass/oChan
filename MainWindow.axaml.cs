using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;

namespace oChan
{
    public partial class MainWindow : Window
    {
        // ObservableCollection to store the IThread objects and bind them to the DataGrid
        public ObservableCollection<IThread> UrlList { get; set; }

        // Registry to manage different image boards
        private Registry _Registry;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize the registry
            _Registry = new Registry();

            // Initialize the UrlList for the DataGrid
            UrlList = new ObservableCollection<IThread>();

            this.DataContext = this;
        }

        // Event handler for Add to List button
        private async void OnAddUrl(object sender, RoutedEventArgs e)
        {
            // Get the URL from the input box, ensuring it's not null
            string url = UrlInput.Text ?? string.Empty;

            // Check if the URL is not empty
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    IThread? thread = _Registry.HandleUrl(url);
                    if (thread != null)
                    {
                        // Add the thread to the list for display in the DataGrid
                        UrlList.Add(thread);

                        // Initialize the ArchiveOptions with a shared DownloadQueue
                        ArchiveOptions options = new ArchiveOptions
                        {
                            DownloadQueue = new DownloadQueue(5, 1024 * 1024 * 10) // Adjust parallel downloads and bandwidth limit as needed
                        };

                        // Start the archive process asynchronously
                        await thread.ArchiveAsync(options);

                        // The UI will update automatically due to data binding and INotifyPropertyChanged
                    }
                    else
                    {
                        Log.Warning("No image board could handle the URL: {Url}", url);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An error occurred while handling the URL: {Url}", url);
                    // Display an error message to the user if necessary
                }

                // Clear the input box after adding the URL
                UrlInput.Text = string.Empty;
            }
        }
    }
}

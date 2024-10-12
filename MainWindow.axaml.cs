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

            // Initialize logging (if not already initialized)
            // If logging is initialized in Registry, you can skip this
            // Log.Logger = new LoggerConfiguration()
            //     .MinimumLevel.Debug()
            //     .WriteTo.Console()
            //     .CreateLogger();

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
                    var thread = _Registry.HandleUrl(url);
                    if (thread != null)
                    {
                        // Add the thread to the list for display in the DataGrid
                        UrlList.Add(thread);

                        // Optionally, start the download or archive process
                        var options = new ArchiveOptions();
                        await thread.ArchiveAsync(options);

                        // Update UI if necessary
                        // If IThread implements INotifyPropertyChanged, the UI will update automatically
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

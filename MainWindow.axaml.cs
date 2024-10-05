using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using System;
using oChan.Downloaders;


namespace oChan
{
    public partial class MainWindow : Window
    {
        // ObservableCollection to store the Downloader objects and bind them to the DataGrid
        public ObservableCollection<Downloader> UrlList { get; set; }

        // Registry to manage different downloader types
        private Registry _Registry;

        public MainWindow()
        {

            // Initialize the registry and register downloaders
            _Registry = new Registry();
            _Registry.RegisterDownloader(new FourChanDownloader());

            // Initialize the UrlList for the DataGrid
            UrlList = new ObservableCollection<Downloader>
            {
                new FourChanDownloader { Url = "https://boards.4chan.org/thread1", Progress = "5/10", Status = "Complete" },
                new FourChanDownloader { Url = "https://boards.4chan.org/thread2", Progress = "3/5", Status = "In Progress" },
                new FourChanDownloader { Url = "https://boards.4chan.org/thread3", Progress = "0/0", Status = "Idle" }
            };

            this.DataContext = this;
            InitializeComponent();

        }

        // Event handler for Add to List button
        private void OnAddUrl(object sender, RoutedEventArgs e)
        {
            // Get the URL from the input box, ensuring it's not null
            string url = UrlInput.Text ?? string.Empty;

            // Check if the URL is not empty
            if (!string.IsNullOrEmpty(url))
            {
                var downloader = _Registry.HandleUrl(url);
                if (downloader != null)
                {
                    // Add the downloader to the list for display in the DataGrid
                    UrlList.Add(downloader);
                }

                // Clear the input box after adding the URL
                UrlInput.Text = string.Empty;
            }
        }
    }
}

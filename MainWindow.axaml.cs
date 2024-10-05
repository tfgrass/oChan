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
    InitializeComponent();
    
    // Initialize the registry and register downloaders
    _Registry = new Registry();
    
    var fourChanDownloader = new FourChanDownloader();

    // Set the delegate to handle UI updates
    fourChanDownloader.UpdateUi = UpdateDownloaderInList;

    _Registry.RegisterDownloader(fourChanDownloader);

    // Initialize the UrlList for the DataGrid
    UrlList = new ObservableCollection<Downloader>();

    this.DataContext = this;
}

private void UpdateDownloaderInList(Downloader downloader)
{
    // Find the downloader in the list and refresh its properties
    var index = UrlList.IndexOf(downloader);
    if (index >= 0)
    {
        UrlList.RemoveAt(index);
        UrlList.Insert(index, downloader); // Re-add to force UI update
    }
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

using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using System;
namespace oChan
{
    public partial class MainWindow : Window
    {
        // ObservableCollection to store the URL data and bind it to the DataGrid
        public ObservableCollection<UrlInfo> UrlList { get; set; }

public MainWindow()
{
 
    // Initialize UrlList with sample data for testing
    UrlList = new ObservableCollection<UrlInfo>
    {
        new UrlInfo { Url = "https://example.com/thread1", ImagesDownloaded = "5/10", Status = "Complete" },
        new UrlInfo { Url = "https://example.com/thread2", ImagesDownloaded = "3/5", Status = "In Progress" },
        new UrlInfo { Url = "https://example.com/thread3", ImagesDownloaded = "0/0", Status = "Idle" }
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
                // Add a new UrlInfo object to the UrlList
                UrlList.Add(new UrlInfo
                {
                    Url = url,                // Assign the URL from the input
                    ImagesDownloaded = "0/0", // Default value for images downloaded
                    Status = "Idle"           // Default status as 'Idle'
                });
        foreach (var item in UrlList)
        {
            Console.WriteLine($"Url: {item.Url}, ImagesDownloaded: {item.ImagesDownloaded}, Status: {item.Status}");
        }
                // Clear the input box after adding the URL
                UrlInput.Text = string.Empty;
            }
        }
    }

    public class UrlInfo
    {
        public string Url { get; set; } = string.Empty; 
        public string ImagesDownloaded { get; set; } = "0/0"; 
        public string Status { get; set; } = "Idle";
    }
}

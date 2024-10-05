using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;

namespace oChan
{
    public partial class MainWindow : Window
    {
        // ObservableCollection to store the URL data and bind it to the DataGrid
        public ObservableCollection<UrlInfo> UrlList { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            // Initialize UrlList
            UrlList = new ObservableCollection<UrlInfo>();
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

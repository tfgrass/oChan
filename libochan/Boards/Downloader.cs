using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace oChan.Boards
{
    public abstract class Downloader : INotifyPropertyChanged
    {
        public delegate void UpdateUiDelegate(Downloader downloader);
        public UpdateUiDelegate? UpdateUi { get; set; } // Delegate for UI updates

        private static readonly SemaphoreSlim _rateLimiter = new(1, 1);  // Limit to 1 request per second
        private static readonly ConcurrentQueue<string> _downloadQueue = new();

        private string? _url;
        private string _progress = "0/0";
        private string _status = "Idle";

        public string? Url
        {
            get => _url;
            set
            {
                _url = value;
                OnPropertyChanged();
                UpdateUi?.Invoke(this);  // Invoke the delegate when Url changes
            }
        }

        public string Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                OnPropertyChanged();
                UpdateUi?.Invoke(this);  // Invoke the delegate when Progress changes
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                UpdateUi?.Invoke(this);  // Invoke the delegate when Status changes
            }
        }

        // Abstract method for actual downloading logic
        public abstract Task DownloadThreadAsync(string url);

        // Method to queue a URL for download
        public void QueueDownload(string url)
        {
            _downloadQueue.Enqueue(url);
            if (Status == "Idle")
            {
                StartQueue();  // Start processing the queue if idle
            }
        }

        public abstract bool CanHandle(string url);

        // Start processing the download queue
        private async void StartQueue()
        {
            Status = "Working";

            while (!_downloadQueue.IsEmpty)
            {
                if (_downloadQueue.TryDequeue(out var threadUrl))
                {
                    await _rateLimiter.WaitAsync();  // Enforce rate limiting
                    try
                    {
                        await DownloadThreadAsync(threadUrl);  // Perform the download
                    }
                    finally
                    {
                        _rateLimiter.Release();  // Release for the next task
                    }
                }
            }

            Status = "Idle";  // Done with the queue
        }

        // PropertyChanged mechanism
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

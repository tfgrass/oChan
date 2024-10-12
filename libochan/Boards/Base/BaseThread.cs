using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;

namespace oChan.Boards.Base
{
    public abstract class BaseThread : IThread, INotifyPropertyChanged
    {
        public abstract IBoard Board { get; }
        public abstract string ThreadId { get; }

        private string _title;
        public virtual string Title
        { 
            get => _title; 
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public abstract string NiceName { get; }
        public abstract Uri ThreadUri { get; }

        private string _status;
        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    Log.Information("Thread {ThreadId} status changed to: {Status}", ThreadId, _status);
                }
            }
        }

        private int _totalMediaCount;
        public int TotalMediaCount
        {
            get => _totalMediaCount;
            set
            {
                if (_totalMediaCount != value)
                {
                    _totalMediaCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        public int DownloadedMediaCount => DownloadedMedia.Count;

        public string Progress
        {
            get
            {
                if (TotalMediaCount == 0)
                    return "0 / 0 (0%)";
                double percent = (double)DownloadedMediaCount / TotalMediaCount * 100;
                return $"{DownloadedMediaCount} / {TotalMediaCount} ({percent:0.##}%)";
            }
        }

        public string Url => ThreadUri.ToString();

        public HashSet<string> DownloadedMedia { get; set; } = new HashSet<string>();

        public virtual async Task ArchiveAsync(ArchiveOptions options)
        {
            Log.Information("Archiving thread {ThreadId} with options {Options}", ThreadId, options);
            await EnqueueMediaDownloadsAsync(options.DownloadQueue);
        }

        public virtual async Task EnqueueMediaDownloadsAsync(DownloadQueue queue)
        {
            Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);
            await Task.CompletedTask;
        }

        public virtual bool IsMediaDownloaded(string mediaIdentifier)
        {
            bool isDownloaded = DownloadedMedia.Contains(mediaIdentifier);
            Log.Debug("Media {MediaId} downloaded: {IsDownloaded}", mediaIdentifier, isDownloaded);
            return isDownloaded;
        }

        public virtual void MarkMediaAsDownloaded(string mediaIdentifier)
        {
            if (DownloadedMedia.Add(mediaIdentifier))
            {
                OnPropertyChanged(nameof(DownloadedMediaCount));
                OnPropertyChanged(nameof(Progress));

                if (DownloadedMediaCount == TotalMediaCount)
                {
                    Status = "Finished";
                }
            }
        }

        // Implementing INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

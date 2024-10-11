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
        public virtual string Title  // Removed override, now simply implements the interface
        { 
            get => _title; 
            set
            {
                _title = value;
                OnPropertyChanged();
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
                _status = value;
                OnPropertyChanged();
                Log.Information("Thread {ThreadId} status changed to: {Status}", ThreadId, _status);
            }
        }

        public string Progress => "0%";  // Placeholder for actual progress logic

        public string Url => ThreadUri.ToString();

        public HashSet<string> DownloadedMedia { get; set; } = new HashSet<string>();

        public virtual async Task ArchiveAsync(ArchiveOptions options)
        {
            Log.Information("Archiving thread {ThreadId} with options {Options}", ThreadId, options);
            await Task.CompletedTask;
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
            Log.Debug("Marking media {MediaId} as downloaded", mediaIdentifier);
            DownloadedMedia.Add(mediaIdentifier);
        }

        // Implementing INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

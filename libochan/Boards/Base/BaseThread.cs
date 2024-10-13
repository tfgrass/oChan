namespace oChan.Boards.Base;

using System;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;
using Avalonia.Threading; // Make sure to include this namespace

public abstract class BaseThread : IThread, INotifyPropertyChanged
{
    // Event that notifies when the thread should be removed
    public event Action<IThread>? ThreadRemoved;

    private Rechecker _rechecker;
    private int _recheckIntervalInSeconds = 60; // Default recheck interval

    public abstract IBoard Board { get; }
    public abstract string ThreadId { get; }

    private string _title = "Unknown";
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

    private string _status = "Pending";
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
        await Task.CompletedTask;
    }

    public virtual async Task checkThreadAsync(DownloadQueue queue)
    {
        Status = "Checking";
        Log.Information("Starting recheck for thread {ThreadId}", ThreadId);

        // This method is for logging purposes, and the actual checking will be done in the derived class
        await Task.CompletedTask;

        // After rechecking, if no new downloads are added, set the status back to "Finished"
        if (DownloadedMediaCount == TotalMediaCount)
        {
            Status = "Finished";
        }

        Log.Information("Finished rechecking thread {ThreadId}", ThreadId);
    }

    public virtual async Task EnqueueMediaDownloadsAsync(DownloadQueue queue)
    {
        Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);
        await Task.CompletedTask;
    }

    public bool IsMediaDownloaded(string mediaIdentifier)
    {
        bool isDownloaded = DownloadedMedia.Contains(mediaIdentifier);
        Log.Debug("Media {MediaId} downloaded: {IsDownloaded}", mediaIdentifier, isDownloaded);
        return isDownloaded;
    }

    public void MarkMediaAsDownloaded(string mediaIdentifier)
    {
        // Ensure that the media is not already in the downloaded set before adding
        if (!DownloadedMedia.Contains(mediaIdentifier) && DownloadedMedia.Add(mediaIdentifier))
        {
            OnPropertyChanged(nameof(DownloadedMediaCount));
            OnPropertyChanged(nameof(Progress));

            if (DownloadedMediaCount == TotalMediaCount)
            {
                Status = "Finished";
            }
        }
        else
        {
            Log.Warning("Media {MediaId} already marked as downloaded for thread {ThreadId}", mediaIdentifier, ThreadId);
        }
    }

    public async Task LoadDownloadedMediaAsync()
    {
        string filePath = Path.Combine("Downloads", Board.BoardCode, ThreadId, ".downloaded.json");

        if (File.Exists(filePath))
        {
            try
            {
                string jsonContent = await File.ReadAllTextAsync(filePath);
                HashSet<string>? downloadedMedia = JsonSerializer.Deserialize<HashSet<string>>(jsonContent);
                if (downloadedMedia != null)
                {
                    DownloadedMedia = downloadedMedia;
                }

                Log.Information("Loaded downloaded media for thread {ThreadId} from {FilePath}", ThreadId, filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading downloaded media for thread {ThreadId} from {FilePath}", ThreadId, filePath);
            }
        }
    }

    public async Task SaveDownloadedMediaAsync()
    {
        string directoryPath = Path.Combine("Downloads", Board.BoardCode, ThreadId);
        string filePath = Path.Combine(directoryPath, ".downloaded.json");

        try
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            string jsonContent = JsonSerializer.Serialize(DownloadedMedia);
            await File.WriteAllTextAsync(filePath, jsonContent);

            Log.Information("Saved downloaded media for thread {ThreadId} to {FilePath}", ThreadId, filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving downloaded media for thread {ThreadId} to {FilePath}", ThreadId, filePath);
        }
    }

    protected BaseThread()
    {
        StartRechecking(_recheckIntervalInSeconds);
    }

    public void StartRechecking(int intervalInSeconds)
    {
        if (_rechecker == null)
        {
            Log.Information("Starting immediate check for thread {ThreadId}", ThreadId);

            Task.Run(async () =>
            {
                DownloadQueue queue = new DownloadQueue(5, 1024 * 1024); // Example queue
                await checkThreadAsync(queue);
            });

            Log.Information("Starting rechecking for thread {ThreadId} with interval {IntervalInSeconds} seconds", ThreadId, intervalInSeconds);
            _recheckIntervalInSeconds = intervalInSeconds;

            _rechecker = new Rechecker(new List<IThread> { this }, _recheckIntervalInSeconds);
        }
        else
        {
            Log.Warning("Rechecker already running for thread {ThreadId}", ThreadId);
        }
    }

    public void StopRechecking()
    {
        if (_rechecker != null)
        {
            _rechecker.StopRechecking();
            _rechecker = null;
            Log.Information("Stopped rechecking for thread {ThreadId}", ThreadId);
        }
    }

    protected virtual int DefaultRecheckInterval => _recheckIntervalInSeconds;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            });
        }
    }

    public void NotifyThreadRemoval()
    {
        Log.Information("Notifying that thread {ThreadId} should be removed", ThreadId);
        ThreadRemoved?.Invoke(this); // Notify whoever is subscribed to this event (UI or Registry)
    }
}

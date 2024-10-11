namespace oChan.Boards.Base;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;

public abstract class BaseThread : IThread
{
    public abstract IBoard Board { get; }
    public abstract string ThreadId { get; }
    public abstract string Title { get; }
    public abstract string NiceName { get; }
    public abstract Uri ThreadUri { get; }

    public HashSet<string> DownloadedMedia { get; set; } = new HashSet<string>();

    public virtual async Task ArchiveAsync(ArchiveOptions options)
    {
        Log.Information("Archiving thread {ThreadId} with options {Options}", ThreadId, options);
        // Implement archiving logic
        await Task.CompletedTask;
    }

    public virtual async Task EnqueueMediaDownloadsAsync(DownloadQueue queue)
    {
        Log.Debug("Enqueuing media downloads for thread {ThreadId}", ThreadId);
        // Implement enqueue logic
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
}

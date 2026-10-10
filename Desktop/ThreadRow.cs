using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Desktop;

/// <summary>Projects a thread's changing state onto the desktop dispatcher.</summary>
public sealed class ThreadRow : INotifyPropertyChanged, IDisposable
{
    private readonly CoalescedUiUpdate _refresh;
    private DownloadCounts _downloads;
    private bool _disposed;

    public IThread Source { get; }
    public string HostName { get; private set; } = string.Empty;
    public string BoardName { get; private set; } = string.Empty;
    public string ThreadId { get; private set; } = string.Empty;
    public long? ThreadNumber { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string Progress { get; private set; } = string.Empty;
    public string ErrorDetails { get; private set; } = string.Empty;
    public string StatusDetails => string.IsNullOrEmpty(ErrorDetails) ? Status : Status + Environment.NewLine + ErrorDetails;
    public double Completion { get; private set; }
    public int Queued { get; private set; }
    public int Active { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThreadRow(IThread source)
    {
        Source = source;
        _refresh = new CoalescedUiUpdate(Refresh);
        Source.PropertyChanged += OnSourceChanged;
        Source.Board.PropertyChanged += OnSourceChanged;

        Refresh();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => _refresh.Request();

    private void Refresh() => UpdateSnapshot(Source.GetSnapshot(_downloads));

    public void UpdateSnapshot(ThreadSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        HostName = Source.Board.ImageBoard.NiceName;
        BoardName = Source.Board.NiceName;
        ThreadId = Source.ThreadId;
        ThreadNumber = long.TryParse(ThreadId, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : null;
        Title = snapshot.Title;
        string activity = snapshot.Activity switch
        {
            ThreadActivity.Pending => "Pending",
            ThreadActivity.Checking => "Checking",
            ThreadActivity.Downloading => "Downloading",
            ThreadActivity.Finished => "Finished",
            ThreadActivity.NoMedia => "No media",
            ThreadActivity.Error => "Error",
            ThreadActivity.NotFound => "Not found",
            ThreadActivity.Stopped => "Stopped",
            _ => "Unknown"
        };
        string? request = RequestStatus.Format(snapshot.RequestProgress);
        if (request != null && request != activity)
        {
            activity += " · " + request;
        }

        if (snapshot.LastCheckFailure != null)
        {
            activity = "Check failed · " + activity;
        }

        Status = snapshot.SourceState switch
        {
            ThreadSourceState.Archived => "Archived · " + activity,
            ThreadSourceState.NotFound => "No longer available · " + activity,
            _ => activity
        };
        Completion = snapshot.KnownMedia == 0 ? 0 : 100d * snapshot.CompletedMedia / snapshot.KnownMedia;
        Progress = $"{snapshot.CompletedMedia} / {snapshot.KnownMedia} ({Completion:0.##}%)";
        _downloads = snapshot.Downloads;
        Queued = _downloads.Queued;
        Active = _downloads.Active;
        string[] mediaErrors = snapshot.MediaFailures.Values.Select(mediaFailure => mediaFailure.Message).Distinct().Take(5).ToArray();
        ErrorDetails = string.Join(Environment.NewLine, mediaErrors);
        if (snapshot.LastCheckFailure is FailureInfo failure)
        {
            ErrorDetails = string.IsNullOrEmpty(ErrorDetails) ? failure.Message : failure.Message + Environment.NewLine + ErrorDetails;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Dispose()
    {
        _disposed = true;
        _refresh.Dispose();
        Source.PropertyChanged -= OnSourceChanged;
        Source.Board.PropertyChanged -= OnSourceChanged;
    }
}

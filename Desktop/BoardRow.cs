using System;
using System.ComponentModel;
using oChan.Interfaces;

namespace oChan.Desktop;

/// <summary>Projects board metadata without exposing worker-thread notifications to bindings.</summary>
public sealed class BoardRow : INotifyPropertyChanged, IDisposable
{
    private readonly CoalescedUiUpdate _refresh;
    private bool _disposed;

    public IBoard Source { get; }
    public string HostName { get; private set; } = string.Empty;
    public string NiceName { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string ErrorDetails { get; private set; } = string.Empty;
    public string DownloadDirectory { get; private set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BoardRow(IBoard source)
    {
        Source = source;
        _refresh = new CoalescedUiUpdate(Refresh);
        Source.PropertyChanged += OnSourceChanged;

        Refresh();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => _refresh.Request();

    private void Refresh() => UpdateSnapshot(Source.GetSnapshot(DownloadDirectory));

    public void UpdateSnapshot(BoardSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        HostName = Source.ImageBoard.NiceName;
        NiceName = Source.NiceName;
        DownloadDirectory = snapshot.DownloadDirectory;
        string? request = RequestStatus.Format(snapshot.RequestProgress);
        Status = snapshot.LastCheckFailure == null ? request ?? "Monitoring"
            : request == null ? "Error" : "Error · " + request;
        ErrorDetails = snapshot.LastCheckFailure?.Message ?? string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Dispose()
    {
        _disposed = true;
        _refresh.Dispose();
        Source.PropertyChanged -= OnSourceChanged;
    }
}

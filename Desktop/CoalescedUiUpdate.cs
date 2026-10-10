using System;
using System.Threading;
using Avalonia.Threading;

namespace oChan.Desktop;

/// <summary>At most one pending UI update, with a fresh read after each burst of changes.</summary>
internal sealed class CoalescedUiUpdate : IDisposable
{
    private readonly Action _update;
    private int _pending;
    private volatile bool _disposed;

    public CoalescedUiUpdate(Action update)
    {
        _update = update;
    }

    public void Request()
    {
        if (_disposed || Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        // A change during the snapshot read must be able to schedule another update.
        Interlocked.Exchange(ref _pending, 0);
        if (!_disposed)
        {
            _update();
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

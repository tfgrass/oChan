using System;
using System.Threading;
using System.Threading.Tasks;

namespace oChan;

/// <summary>
/// Forwards cancellation without running an I/O implementation's callbacks on a
/// deadline continuation. Disposal unlinks the parents immediately, but retains
/// the owned source until its one cancellation task has finished its callbacks.
/// </summary>
internal sealed class AsyncCancellationLink : IDisposable
{
    private readonly object _sync = new object();
    private readonly CancellationTokenSource _source = new CancellationTokenSource();
    private readonly CancellationTokenRegistration[] _registrations;
    private Task? _cancellation;
    private bool _disposed;

    public AsyncCancellationLink(params CancellationToken[] parents)
    {
        _registrations = new CancellationTokenRegistration[parents.Length];
        for (int index = 0; index < parents.Length; index++)
            _registrations[index] = parents[index].UnsafeRegister(
                static state => ((AsyncCancellationLink)state!).RequestCancellation(), this);
    }

    public CancellationToken Token => _source.Token;

    public void RequestCancellation()
    {
        lock (_sync)
        {
            if (_disposed) return;
            // The source is private: every cancellation uses this same task,
            // including one already requested by a parent on another thread.
            _cancellation ??= _source.CancelAsync();
        }
    }

    public void Dispose()
    {
        Task? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cancellation = _cancellation;
        }

        // A parent callback can be waiting for _sync. Never unregister while
        // holding that lock; callbacks only schedule cancellation and return.
        foreach (CancellationTokenRegistration registration in _registrations)
            registration.Dispose();

        if (cancellation == null || cancellation.IsCompletedSuccessfully)
            _source.Dispose();
        else
            _ = DisposeAfterCancellationAsync(_source, cancellation);
    }

    private static async Task DisposeAfterCancellationAsync(CancellationTokenSource source, Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        // Observe failures from foreign callbacks without replacing the I/O
        // operation's own cancellation or timeout result.
        catch (Exception) { }
        finally { source.Dispose(); }
    }
}

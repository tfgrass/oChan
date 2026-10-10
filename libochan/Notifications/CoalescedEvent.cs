using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace oChan.Notifications;

/// <summary>
/// Delivers state invalidations asynchronously with one active invocation and one
/// pending invalidation per subscription. A subscription cannot delay another.
/// </summary>
internal sealed class CoalescedEvent<THandler> : IDisposable where THandler : Delegate
{
    private readonly object _gate = new object();
    private readonly List<Subscription> _subscriptions = new List<Subscription>();
    private readonly Action<THandler> _invoke;
    private readonly Action<Exception>? _reportFailure;
    private bool _disposed;

    public CoalescedEvent(Action<THandler> invoke, Action<Exception>? reportFailure = null)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        _invoke = invoke;
        _reportFailure = reportFailure;
    }

    public void Add(THandler? handler)
    {
        if (handler == null) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (Delegate invocation in handler.GetInvocationList())
                _subscriptions.Add(new Subscription((THandler)invocation, _invoke, _reportFailure));
        }
    }

    public void Remove(THandler? handler)
    {
        if (handler == null) return;
        lock (_gate)
        {
            Delegate[] invocations = handler.GetInvocationList();
            for (int start = _subscriptions.Count - invocations.Length; start >= 0; start--)
            {
                bool matches = true;
                for (int offset = 0; offset < invocations.Length; offset++)
                {
                    if (_subscriptions[start + offset].Handler.Equals(invocations[offset])) continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                for (int offset = 0; offset < invocations.Length; offset++)
                    _subscriptions[start + offset].Dispose();
                _subscriptions.RemoveRange(start, invocations.Length);
                return;
            }
        }
    }

    public void Signal()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            subscriptions = _subscriptions.ToArray();
        }
        foreach (Subscription subscription in subscriptions) subscription.Signal();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Subscription subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<THandler> _invoke;
        private readonly Action<Exception>? _reportFailure;
        private bool _pending;
        private bool _running;
        private bool _disposed;

        public THandler Handler { get; }

        public Subscription(THandler handler, Action<THandler> invoke, Action<Exception>? reportFailure)
        {
            Handler = handler;
            _invoke = invoke;
            _reportFailure = reportFailure;
        }

        public void Signal()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _pending = true;
                if (_running) return;
                _running = true;
            }
            _ = Task.Run(Drain);
        }

        private void Drain()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposed || !_pending)
                    {
                        _running = false;
                        return;
                    }
                    _pending = false;
                }

                try { _invoke(Handler); }
                catch (Exception exception)
                {
                    try { _reportFailure?.Invoke(exception); }
                    catch (Exception) { /* A diagnostic sink cannot fault the delivery task. */ }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _pending = false;
            }
        }
    }
}

using System;

namespace oChan.Notifications;

/// <summary>Exclusive reliable provider controlplane; this is not an observer event.</summary>
internal sealed class SingleOwnerCallback<THandler> where THandler : Delegate
{
    private readonly object _gate = new object();
    private Registration? _registration;

    public THandler? Current
    {
        get { lock (_gate) { return _registration?.Handler; } }
    }

    public IDisposable Register(THandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_registration != null)
                throw new InvalidOperationException("This resource already has a session owner.");
            Registration registration = new Registration(this, handler);
            _registration = registration;
            return registration;
        }
    }

    private void Release(Registration registration)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_registration, registration)) _registration = null;
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly SingleOwnerCallback<THandler> _owner;
        public THandler Handler { get; }

        public Registration(SingleOwnerCallback<THandler> owner, THandler handler)
        {
            _owner = owner;
            Handler = handler;
        }

        public void Dispose() => _owner.Release(this);
    }
}

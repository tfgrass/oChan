using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace oChan.Downloader;

/// <summary>
/// Borrows a response stream and limits each asynchronous read. Time spent between
/// reads, including bandwidth admission and disk writes, does not consume this limit.
/// The caller retains ownership of the underlying stream.
/// </summary>
internal sealed class IdleTimeoutReadStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _timeProvider;

    public IdleTimeoutReadStream(Stream inner, TimeSpan idleTimeout, TimeProvider timeProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ValidateTimeout(idleTimeout, nameof(idleTimeout));
        _idleTimeout = idleTimeout;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1L)
            throw new ArgumentOutOfRangeException(parameterName, "The body idle timeout must be positive and fit within a timer interval.");
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = new CancellationTokenSource(_idleTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Task<int>? read = null;
        try
        {
            ValueTask<int> pending = _inner.ReadAsync(buffer, linked.Token);
            if (pending.IsCompletedSuccessfully)
                return pending.Result;

            read = pending.AsTask();
            // Request caller cancellation in the underlying operation, then let
            // its cooperative cleanup finish. Only our own deadline detaches a
            // read that will not drain; callers can await a meaningful stop.
            return await read.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            if (read != null)
                _ = ObserveLateReadAsync(read);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("The media body did not supply data within the idle timeout.", exception);
            throw;
        }
    }

    private static async Task ObserveLateReadAsync(Task read)
    {
        // A custom stream may ignore cancellation. The worker disposes the owning
        // response after our bounded wait; observe any subsequently reported error.
        try { await read.ConfigureAwait(false); }
        catch (Exception) { }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads.");
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

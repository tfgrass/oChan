using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using oChan.Downloader;

namespace oChan.Http;

/// <summary>
/// Reads a JSON response obtained with ResponseHeadersRead, with a deadline for
/// stream acquisition, each read and the complete body. Bodies are limited to
/// 32 MiB by default, decoded as UTF-8 with BOM detection, and always disposed.
/// Caller cancellation waits for cooperative cleanup within these deadlines.
/// The supplied content remains owned by the caller.
/// </summary>
public sealed class JsonResponseReader
{
    private readonly TimeSpan _bodyIdleTimeout;
    private readonly TimeSpan _bodyTotalTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxBytes;

    public JsonResponseReader(TimeSpan? bodyIdleTimeout = null, TimeSpan? bodyTotalTimeout = null,
        TimeProvider? timeProvider = null, long maxBytes = 32L * 1024 * 1024)
    {
        _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
        _bodyTotalTimeout = bodyTotalTimeout ?? TimeSpan.FromSeconds(60);
        ValidateTimeout(_bodyIdleTimeout, nameof(bodyIdleTimeout));
        ValidateTimeout(_bodyTotalTimeout, nameof(bodyTotalTimeout));
        if (maxBytes <= 0 || maxBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The body byte limit must be positive and fit within a managed string.");

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxBytes = maxBytes;
    }

    public async Task<string> ReadAsync(HttpContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource totalDeadline = new CancellationTokenSource(_bodyTotalTimeout, _timeProvider);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalDeadline.Token);
        try
        {
            using Stream body = await AcquireBodyAsync(content, operation.Token, totalDeadline.Token).ConfigureAwait(false);
            using IdleTimeoutReadStream timedBody = new IdleTimeoutReadStream(body, _bodyIdleTimeout, _timeProvider);
            using LimitedReadStream limitedBody = new LimitedReadStream(timedBody, _maxBytes);
            using StreamReader reader = new StreamReader(limitedBody, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);

            Task<string> reading = reader.ReadToEndAsync(operation.Token);
            try
            {
                // A caller's cancellation is delivered to the underlying read,
                // whose cleanup is awaited. Only our own total deadline may
                // detach that cleanup; it must not gain another idle interval.
                string result = await reading.WaitAsync(totalDeadline.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException)
            {
                _ = ObserveLateReadAsync(reading);
                throw;
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
            && (exception is OperationCanceledException || exception is TimeoutException))
        {
            throw new OperationCanceledException("Reading the JSON response was cancelled.", exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (totalDeadline.IsCancellationRequested)
        {
            throw new TimeoutException("The JSON response body exceeded its total read deadline.", exception);
        }
    }

    private async Task<Stream> AcquireBodyAsync(HttpContent content, CancellationToken operationToken,
        CancellationToken totalDeadlineToken)
    {
        using CancellationTokenSource idleDeadline = new CancellationTokenSource(_bodyIdleTimeout, _timeProvider);
        using CancellationTokenSource ownDeadline = CancellationTokenSource.CreateLinkedTokenSource(totalDeadlineToken, idleDeadline.Token);
        using CancellationTokenSource acquisition = CancellationTokenSource.CreateLinkedTokenSource(operationToken, idleDeadline.Token);
        Task<Stream>? pending = null;
        Stream? body = null;
        try
        {
            pending = content.ReadAsStreamAsync(acquisition.Token);
            // Preserve ownership while cancellation-aware content drains. A
            // non-cooperative acquisition remains bounded by our own deadlines.
            body = await pending.WaitAsync(ownDeadline.Token).ConfigureAwait(false);
            acquisition.Token.ThrowIfCancellationRequested();
            return body;
        }
        catch (OperationCanceledException exception)
        {
            if (body != null)
                body.Dispose();
            else if (pending != null)
                _ = DisposeLateBodyAsync(pending);

            operationToken.ThrowIfCancellationRequested();
            if (idleDeadline.IsCancellationRequested)
                throw new TimeoutException("The JSON response body stream was not available within the idle timeout.", exception);
            throw;
        }
    }

    private static async Task DisposeLateBodyAsync(Task<Stream> pending)
    {
        // A custom content implementation may ignore cancellation. Release a
        // stream arriving after our bounded wait and observe any late failure.
        try
        {
            Stream body = await pending.ConfigureAwait(false);
            body.Dispose();
        }
        catch (Exception) { }
    }

    private static async Task ObserveLateReadAsync(Task reading)
    {
        // The owning body is disposed when the total deadline expires. An
        // injected stream may nevertheless complete or fail later.
        try { await reading.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1L)
            throw new ArgumentOutOfRangeException(parameterName, "The timeout must be positive and fit within a timer interval.");
    }

    /// <summary>
    /// Counts bytes before decoding so even a continuous stream cannot grow the
    /// result without bound. Ownership of the underlying stream stays outside.
    /// </summary>
    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remainingBytes;

        public LimitedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _remainingBytes = maxBytes;
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
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count > _remainingBytes)
                throw new InvalidDataException("The JSON response body exceeded its configured byte limit.");
            _remainingBytes -= count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

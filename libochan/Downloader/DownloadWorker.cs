namespace oChan.Downloader;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using oChan.Storage;
using Serilog;

/// <summary>Publishes a medium only after its contents and manifest are committed.</summary>
public sealed class DownloadWorker
{
    private static readonly MediaStore DefaultStore = new MediaStore();
    private readonly DownloadItem _item;
    private readonly BandwidthLimiter _limiter;
    private readonly MediaStore _store;
    private readonly TimeSpan _bodyIdleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public DownloadWorker(DownloadItem downloadItem, BandwidthLimiter bandwidthLimiter, MediaStore? store = null,
        TimeSpan? bodyIdleTimeout = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _item = downloadItem ?? throw new ArgumentNullException(nameof(downloadItem));
        _limiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
        _store = store ?? DefaultStore;
        _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
        IdleTimeoutReadStream.ValidateTimeout(_bodyIdleTimeout, nameof(bodyIdleTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Serilog.Core.Logger.None;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaCommitResult? restored = await _store.TryRestoreAsync(_item, cancellationToken).ConfigureAwait(false);
        if (restored != null)
        {
            _item.Thread.MarkMediaAsDownloaded(_item.MediaIdentifier, _item.DestinationPath);
            return;
        }
        _item.Thread.MarkMediaPending(_item.MediaIdentifier, _item.DestinationPath);

        HttpClient client = _item.ImageBoard.GetHttpClient();
        using HttpResponseMessage response = await client.GetAsync(_item.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // An unavailable media file is a failed media job, never a thread-removal signal.
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
            throw new InvalidDataException("A partial media response cannot be accepted without range-resume support.");
        long? contentLength = response.Content.Headers.ContentLength;
        if (_item.ExpectedSize.HasValue && contentLength.HasValue && _item.ExpectedSize != contentLength)
            throw new InvalidDataException("HTTP Content-Length conflicts with the provider's expected media size.");

        DownloadItem verifiedItem = _item.ExpectedSize.HasValue || !contentLength.HasValue ? _item : new DownloadItem(
            _item.DownloadUri, _item.DestinationPath, _item.ImageBoard, _item.Thread, _item.MediaIdentifier,
            contentLength, _item.ExpectedMd5, _item.OriginalFileName, _item.LegacyPath);
        await using Stream source = await OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using IdleTimeoutReadStream timedSource = new IdleTimeoutReadStream(source, _bodyIdleTimeout, _timeProvider);
        MediaCommitResult completed = await _store.CommitAsync(verifiedItem, timedSource, cancellationToken,
            _limiter.ThrottleAsync).ConfigureAwait(false);
        // CommitAsync deliberately finishes its short manifest commit after publishing the file,
        // even when cancellation arrives then. This is a verified completion, not a partial file.
        _item.Thread.MarkMediaAsDownloaded(_item.MediaIdentifier, _item.DestinationPath);
        _logger.Debug("Saved verified media {MediaId} to {Path} ({Length} bytes)",
            _item.MediaIdentifier, completed.Path, completed.Length);
    }

    private async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = new CancellationTokenSource(_bodyIdleTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Task<Stream>? opening = null;
        try
        {
            opening = content.ReadAsStreamAsync(linked.Token);
            return await opening.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            if (opening != null)
                _ = DisposeLateBodyAsync(opening);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("The media response did not provide its body stream within the idle timeout.", exception);
            throw;
        }
    }

    private static async Task DisposeLateBodyAsync(Task<Stream> opening)
    {
        // HttpContent implementations may ignore the supplied token. A stream
        // produced after the bounded wait still needs an owner to dispose it.
        try
        {
            await using Stream stream = await opening.ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}

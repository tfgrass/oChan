using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using oChan.Boards.Base;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Http;
using oChan.Storage;

namespace oChan.Boards.FourChan;

public class FourChanThread : BaseThread
{
    private readonly Dictionary<string, DownloadItem> _knownMedia = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset? _lastModified;
    private bool _hasSnapshot;

    public override IBoard Board { get; }
    public override string ThreadId { get; }
    public override string NiceName => $"{Title} ({ThreadId})";
    public override Uri ThreadUri { get; }
    protected override TimeSpan MinimumRecheckInterval => TimeSpan.FromSeconds(10);

    public FourChanThread(IBoard board, Uri threadUri)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        ThreadUri = FourChanImageBoard.CanonicalizeThreadUri(threadUri);
        if (!string.Equals(Board.BoardCode, ThreadUri.Segments[1].Trim('/'), StringComparison.Ordinal))
            throw new ArgumentException("Thread URI does not belong to the supplied board.", nameof(threadUri));
        ThreadId = ThreadUri.Segments[3];
        Title = $"Thread {ThreadId}";
    }

    public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            await CheckCoreAsync(queue, cancellationToken);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task CheckCoreAsync(DownloadQueue queue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await base.checkThreadAsync(queue, cancellationToken);
        if (SourceState != ThreadSourceState.Active)
        {
            EnqueueKnownMedia(queue, cancellationToken);
            return;
        }
        if (Board is FourChanBoard board) await board.EnsureMetadataAsync(cancellationToken);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
            $"https://a.4cdn.org/{Board.BoardCode}/thread/{ThreadId}.json");
        request.Options.Set(RequestPolicyHandler.ObserverKey, SetRequestProgress);
        try
        {
            request.Headers.IfModifiedSince = _lastModified;
            using HttpResponseMessage response = await Board.ImageBoard.GetHttpClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!_hasSnapshot) throw new InvalidDataException("A thread returned 304 before a successful snapshot was available.");
                EnqueueKnownMedia(queue, cancellationToken);
                return;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                EnqueueKnownMedia(queue, cancellationToken);
                MarkPollingTerminal(ThreadSourceState.NotFound);
                if (TotalMediaCount == 0 && MediaFailures.Count == 0) Activity = ThreadActivity.NotFound;
                return;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("A thread snapshot requires a complete HTTP 200 response.");
            JObject data = JObject.Parse(await BaseImageBoard.GetJsonReader(Board.ImageBoard).ReadAsync(response.Content, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            JArray posts = data["posts"] as JArray
                ?? throw new InvalidDataException("Thread data does not contain posts.");
            string? subject = posts.Count > 0 ? WebUtility.HtmlDecode(posts[0].Value<string>("sub")) : null;
            Title = string.IsNullOrWhiteSpace(subject) ? $"Thread {ThreadId}" : subject;

            Dictionary<string, DownloadItem> media = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
            foreach (JToken post in posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (post.Value<int?>("filedeleted") == 1 || post["tim"] == null || post["ext"] == null || post["filename"] == null)
                    continue;
                long timestamp = post.Value<long>("tim");
                string? extension = post.Value<string>("ext");
                if (timestamp <= 0 || string.IsNullOrWhiteSpace(extension)) continue;
                string mediaId = timestamp.ToString(CultureInfo.InvariantCulture);
                string filename = post.Value<string>("filename") ?? "unknown";
                string originalFileName = filename + extension;
                Uri uri = new Uri($"https://i.4cdn.org/{Board.BoardCode}/{mediaId}{extension}");
                string destination = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
                    Board.BoardCode, ThreadId, mediaId, extension);
                string? legacyPath = GetLegacyPath(originalFileName);
                media.TryAdd(mediaId, new DownloadItem(uri, destination, Board.ImageBoard, this, mediaId,
                    expectedSize: post.Value<long?>("fsize"),
                    expectedMd5: post.Value<string>("md5"),
                    originalFileName: originalFileName,
                    legacyPath: legacyPath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (KeyValuePair<string, DownloadItem> item in media) _knownMedia[item.Key] = item.Value;
            _lastModified = response.Content.Headers.LastModified;
            _hasSnapshot = true;
            EnqueueKnownMedia(queue, cancellationToken);
            if (posts.Count > 0 && posts[0].Value<int?>("archived") == 1)
                MarkPollingTerminal(ThreadSourceState.Archived);
        }
        finally
        {
            SetRequestProgress(null);
        }
    }

    private void EnqueueKnownMedia(DownloadQueue queue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string> knownIds = new HashSet<string>(_knownMedia.Keys, StringComparer.Ordinal);
        knownIds.UnionWith(DownloadedMedia);
        TotalMediaCount = knownIds.Count;
        Activity = MediaFailures.Count > 0 ? ThreadActivity.Error : TotalMediaCount == 0
            ? SourceState == ThreadSourceState.NotFound ? ThreadActivity.NotFound : ThreadActivity.NoMedia
            : ThreadActivity.Downloading;
        foreach (DownloadItem item in _knownMedia.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            queue.TryEnqueue(item);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (MediaFailures.Count == 0 && TotalMediaCount > 0 && DownloadedMediaCount >= TotalMediaCount) Activity = ThreadActivity.Finished;
    }

    private string? GetLegacyPath(string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Contains('/') ||
            originalFileName.Contains('\\') || originalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.IsPathRooted(originalFileName)) return null;
        string filename = Path.GetFileName(originalFileName);
        if (filename is "." or ".." || filename != originalFileName) return null;
        return Path.Combine(OutputDirectory, Board.BoardCode, ThreadId, filename);
    }
}

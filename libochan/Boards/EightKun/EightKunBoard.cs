using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using oChan.Boards.Base;
using oChan.Interfaces;
using oChan.Http;

namespace oChan.Boards.EightKun;

public class EightKunBoard : BaseBoard
{
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private bool _metadataLoaded;
    private string _name = string.Empty;

    public override IImageBoard ImageBoard { get; }
    public override string BoardCode { get; }
    public override string Name
    {
        get { lock (StateLock) { return _name; } }
        protected set
        {
            lock (StateLock)
            {
                if (_name == value) return;
                _name = value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(NiceName));
        }
    }
    public override string NiceName => $"/{BoardCode}/ - {Name}";
    public override Uri BoardUri { get; }

    public EightKunBoard(IImageBoard imageBoard, Uri boardUri)
    {
        ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
        BoardUri = EightKunImageBoard.CanonicalizeBoardUri(boardUri);
        BoardCode = BoardUri.AbsolutePath.Trim('/');
        Name = BoardCode;
    }

    internal async Task EnsureMetadataAsync(CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_metadataLoaded) return;
            const string endpoint = "https://8kun.top/boards.json";
            IReadOnlyDictionary<string, string> names = ImageBoard is BaseImageBoard provider
                ? await provider.GetBoardNamesAsync(endpoint, "uri", cancellationToken: cancellationToken)
                : await BaseImageBoard.FetchBoardNamesAsync(ImageBoard.GetHttpClient(), endpoint, "uri", cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (names.TryGetValue(BoardCode, out string? name)) Name = WebUtility.HtmlDecode(name);
            _metadataLoaded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or OperationCanceledException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A display name is optional; failed metadata must not block thread downloads.
            ImageBoard.Logger.Warning(exception, "Could not fetch the name of 8kun board /{BoardCode}/; using its board code", BoardCode);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMetadataAsync(cancellationToken);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"https://8kun.top/{BoardCode}/catalog.json");
        request.Options.Set(RequestPolicyHandler.ObserverKey, SetRequestProgress);
        try
        {
            using HttpResponseMessage response = await ImageBoard.GetHttpClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            JArray catalog = JArray.Parse(await BaseImageBoard.GetJsonReader(ImageBoard).ReadAsync(response.Content, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            List<IThread> threads = new List<IThread>();
            foreach (JToken page in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JArray entries = page["threads"] as JArray
                    ?? throw new InvalidDataException("Catalog page does not contain threads.");
                foreach (JToken entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long id = entry.Value<long?>("no")
                        ?? throw new InvalidDataException("Catalog thread does not contain an ID.");
                    if (id <= 0) throw new InvalidDataException("Catalog thread ID must be positive.");
                    string threadId = id.ToString(CultureInfo.InvariantCulture);
                    EightKunThread thread = new EightKunThread(this, new Uri($"https://8kun.top/{BoardCode}/res/{threadId}.html"));
                    string? subject = WebUtility.HtmlDecode(entry.Value<string>("sub"));
                    thread.Title = string.IsNullOrWhiteSpace(subject) ? $"Thread {threadId}" : subject;
                    threads.Add(thread);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return threads;
        }
        finally
        {
            SetRequestProgress(null);
        }
    }
}

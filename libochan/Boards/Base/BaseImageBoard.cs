using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using oChan.Http;
using oChan.Interfaces;
using Serilog;

namespace oChan.Boards.Base;

/// <summary>Provider identity and its shared HTTP transport.</summary>
public abstract class BaseImageBoard : IImageBoard
{
    private static readonly JsonResponseReader DefaultJsonReader = new JsonResponseReader();
    private readonly HttpClient? _httpClient;
    private readonly JsonResponseReader _jsonReader;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _boardMetadata = new(StringComparer.Ordinal);

    /// <summary>
    /// A supplied client is borrowed unchanged; its caller owns request limits, deadlines and redirect policy.
    /// Use ProviderHttpClientFactory.CreatePolicyClient when a custom transport should use the shared policy.
    /// </summary>
    protected BaseImageBoard(HttpClient? httpClient = null, JsonResponseReader? jsonReader = null, ILogger? logger = null)
    {
        _httpClient = httpClient;
        _jsonReader = jsonReader ?? DefaultJsonReader;
        Logger = logger ?? Serilog.Core.Logger.None;
    }

    /// <summary>Borrowed from the caller; the provider never configures or disposes it.</summary>
    public ILogger Logger { get; }

    public abstract string Name { get; }
    public abstract string NiceName { get; }
    public abstract Uri BaseUri { get; }
    public abstract bool CanHandle(Uri uri);
    public abstract bool IsThreadUri(Uri uri);
    public abstract bool IsBoardUri(Uri uri);
    public abstract IBoard GetBoard(Uri boardUri);
    public abstract IThread GetThread(Uri threadUri);

    public virtual HttpClient GetHttpClient() => _httpClient ?? ProviderHttpClientFactory.GetDefaultClient(Name, Logger);

    internal static JsonResponseReader GetJsonReader(IImageBoard provider) =>
        provider is BaseImageBoard imageBoard ? imageBoard._jsonReader : DefaultJsonReader;

    internal async Task<IReadOnlyDictionary<string, string>> GetBoardNamesAsync(
        string endpoint, string idField, string? collectionField = null, CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_boardMetadata.TryGetValue(endpoint, out IReadOnlyDictionary<string, string>? cached)) return cached;
            IReadOnlyDictionary<string, string> names = await FetchBoardNamesAsync(
                GetHttpClient(), endpoint, idField, collectionField, cancellationToken, _jsonReader);
            cancellationToken.ThrowIfCancellationRequested();
            _boardMetadata.Add(endpoint, names);
            return names;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    internal static async Task<IReadOnlyDictionary<string, string>> FetchBoardNamesAsync(
        HttpClient client, string endpoint, string idField, string? collectionField = null,
        CancellationToken cancellationToken = default, JsonResponseReader? jsonReader = null)
    {
        using HttpResponseMessage response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        JToken data = JToken.Parse(await (jsonReader ?? DefaultJsonReader).ReadAsync(response.Content, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        JArray boards = (collectionField == null ? data : (data as JObject)?[collectionField]) as JArray
            ?? throw new InvalidDataException("Board metadata does not contain a board list.");
        Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JToken board in boards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (board is not JObject record)
                throw new InvalidDataException("Board metadata contains an invalid board entry.");
            if ((record[idField] != null && record[idField]!.Type != JTokenType.String) ||
                (record["title"] != null && record["title"]!.Type != JTokenType.String))
                throw new InvalidDataException("Board metadata identifiers and titles must be strings.");
            string? code = board.Value<string>(idField);
            string? name = board.Value<string>("title");
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name)) names[code] = name;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return names;
    }

    protected static bool IsWebUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

}

using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using oChan.Boards.Base;
using oChan.Interfaces;
using oChan.Http;
using Serilog;

namespace oChan.Boards.EightKun;

public class EightKunImageBoard : BaseImageBoard
{
    private static readonly Uri CanonicalBaseUri = new("https://8kun.top/");
    private static readonly Regex BoardPattern = new(@"^/(?<board>[a-zA-Z0-9_]+)(?:/(?:catalog(?:\.html)?|index\.html))?/?$", RegexOptions.CultureInvariant);
    private static readonly Regex ThreadPattern = new(@"^/(?<board>[a-zA-Z0-9_]+)/res/(?<id>[0-9]+)\.html/?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Uses shared rate and retry controls by default. An injected client is borrowed unchanged;
    /// its owner must provide compliant rate limits, header deadlines and redirect handling.
    /// ProviderHttpClientFactory.CreatePolicyClient can wrap an injected transport with that policy.
    /// </summary>
    public EightKunImageBoard(HttpClient? client = null, JsonResponseReader? jsonReader = null, ILogger? logger = null) : base(client, jsonReader, logger) { }

    public override string Name => "8kun";
    public override string NiceName => "8kun";
    public override Uri BaseUri => CanonicalBaseUri;

    public override bool CanHandle(Uri uri) => IsSupportedUri(uri);
    public override bool IsBoardUri(Uri uri) => CanHandle(uri) && BoardPattern.IsMatch(uri.AbsolutePath);
    public override bool IsThreadUri(Uri uri) => CanHandle(uri) && ThreadPattern.IsMatch(uri.AbsolutePath);

    public override IBoard GetBoard(Uri boardUri)
    {
        Uri canonicalUri = CanonicalizeBoardUri(boardUri);
        return new EightKunBoard(this, canonicalUri);
    }

    public override IThread GetThread(Uri threadUri)
    {
        Uri canonicalUri = CanonicalizeThreadUri(threadUri);
        string boardCode = ThreadPattern.Match(canonicalUri.AbsolutePath).Groups["board"].Value;
        return new EightKunThread(GetBoard(new Uri(CanonicalBaseUri, boardCode + "/")), canonicalUri);
    }

    internal static Uri CanonicalizeBoardUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Match match = IsSupportedUri(uri) ? BoardPattern.Match(uri.AbsolutePath) : Match.Empty;
        if (!match.Success) throw new ArgumentException("Invalid 8kun board URI.", nameof(uri));
        return new Uri(CanonicalBaseUri, match.Groups["board"].Value.ToLowerInvariant() + "/");
    }

    internal static Uri CanonicalizeThreadUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Match match = IsSupportedUri(uri) ? ThreadPattern.Match(uri.AbsolutePath) : Match.Empty;
        if (!match.Success) throw new ArgumentException("Invalid 8kun thread URI.", nameof(uri));
        return new Uri(CanonicalBaseUri,
            $"{match.Groups["board"].Value.ToLowerInvariant()}/res/{match.Groups["id"].Value}.html");
    }

    private static bool IsSupportedUri(Uri? uri) => IsWebUri(uri) &&
        string.Equals(uri!.Host, "8kun.top", StringComparison.OrdinalIgnoreCase);
}

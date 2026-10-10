using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using oChan.Boards.Base;
using oChan.Interfaces;
using oChan.Http;
using Serilog;

namespace oChan.Boards.FourChan;

public class FourChanImageBoard : BaseImageBoard
{
    private static readonly Uri CanonicalBaseUri = new("https://boards.4chan.org/");
    private static readonly Regex BoardPattern = new(@"^/(?<board>[a-zA-Z0-9_]+)(?:/catalog)?/?$", RegexOptions.CultureInvariant);
    private static readonly Regex ThreadPattern = new(@"^/(?<board>[a-zA-Z0-9_]+)/thread/(?<id>[0-9]+)(?:/[^/]+)?/?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Uses shared rate and retry controls by default. An injected client is borrowed unchanged;
    /// its owner must provide compliant rate limits, header deadlines and redirect handling.
    /// ProviderHttpClientFactory.CreatePolicyClient can wrap an injected transport with that policy.
    /// </summary>
    public FourChanImageBoard(HttpClient? client = null, JsonResponseReader? jsonReader = null, ILogger? logger = null) : base(client, jsonReader, logger) { }

    public override string Name => "4chan";
    public override string NiceName => "4chan";
    public override Uri BaseUri => CanonicalBaseUri;

    public override bool CanHandle(Uri uri) => IsSupportedUri(uri);
    public override bool IsBoardUri(Uri uri) => CanHandle(uri) && BoardPattern.IsMatch(uri.AbsolutePath);
    public override bool IsThreadUri(Uri uri) => CanHandle(uri) && ThreadPattern.IsMatch(uri.AbsolutePath);

    public override IBoard GetBoard(Uri boardUri)
    {
        Uri canonicalUri = CanonicalizeBoardUri(boardUri);
        return new FourChanBoard(this, canonicalUri);
    }

    public override IThread GetThread(Uri threadUri)
    {
        Uri canonicalUri = CanonicalizeThreadUri(threadUri);
        string boardCode = ThreadPattern.Match(canonicalUri.AbsolutePath).Groups["board"].Value;
        return new FourChanThread(GetBoard(new Uri(CanonicalBaseUri, boardCode + "/")), canonicalUri);
    }

    internal static Uri CanonicalizeBoardUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Match match = IsSupportedUri(uri) ? BoardPattern.Match(uri.AbsolutePath) : Match.Empty;
        if (!match.Success) throw new ArgumentException("Invalid 4chan board URI.", nameof(uri));
        return new Uri(CanonicalBaseUri, match.Groups["board"].Value.ToLowerInvariant() + "/");
    }

    internal static Uri CanonicalizeThreadUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Match match = IsSupportedUri(uri) ? ThreadPattern.Match(uri.AbsolutePath) : Match.Empty;
        if (!match.Success) throw new ArgumentException("Invalid 4chan thread URI.", nameof(uri));
        return new Uri(CanonicalBaseUri,
            $"{match.Groups["board"].Value.ToLowerInvariant()}/thread/{match.Groups["id"].Value}");
    }

    private static bool IsSupportedUri(Uri? uri) => IsWebUri(uri) &&
        (string.Equals(uri!.Host, "boards.4chan.org", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "boards.4channel.org", StringComparison.OrdinalIgnoreCase));
}

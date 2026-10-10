using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using Serilog;

namespace oChan.Http;

/// <summary>Long-lived provider clients sharing admission and cooldown across all default providers.</summary>
public static class ProviderHttpClientFactory
{
    private static readonly RequestPolicyState SharedPolicy = new RequestPolicyState();
    private static readonly HttpClient DefaultTransport = CreateTransport();
    // Logger identity is local to its consumer, while transport admission stays shared.
    // Weak ownership avoids retaining a finished host through its diagnostic logger.
    private static readonly ConditionalWeakTable<ILogger, ConcurrentDictionary<string, Lazy<HttpClient>>> Clients = new();

    /// <summary>Returns a process-owned client. Callers must not dispose or mutate it.</summary>
    public static HttpClient GetDefaultClient(string source, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ILogger assignedLogger = logger ?? Serilog.Core.Logger.None;
        ConcurrentDictionary<string, Lazy<HttpClient>> providerClients = Clients.GetValue(assignedLogger,
            static _ => new ConcurrentDictionary<string, Lazy<HttpClient>>(StringComparer.Ordinal));
        return providerClients.GetOrAdd(source, name => new Lazy<HttpClient>(
            () => CreatePolicyClient(DefaultTransport, name, SharedPolicy, assignedLogger))).Value;
    }

    /// <summary>
    /// Wraps a borrowed transport without modifying or disposing it. The caller owns the returned client.
    /// The transport must honor cancellation and disable its own automatic redirects and retries.
    /// Pass the same policy state to clients whose request limits and cooldowns must be shared.
    /// </summary>
    public static HttpClient CreatePolicyClient(HttpClient transport, string source, RequestPolicyState? policy = null, ILogger? logger = null)
    {
        HttpClient client = new HttpClient(new RequestPolicyHandler(transport, source, policy ?? SharedPolicy, logger))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("oChan");
        return client;
    }

    private static HttpClient CreateTransport()
    {
        SocketsHttpHandler handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace oChan.Http;

public enum RequestPolicyPhase { WaitingForAdmission, Cooldown, Sending }

public sealed record RequestPolicyProgress(RequestPolicyPhase Phase, TimeSpan? Delay, int Attempt);

/// <summary>
/// Shared admission and cooldown state for the HTTP clients belonging to a session.
/// Elapsed time is monotonic; wall-clock time is used only to interpret Retry-After dates.
/// </summary>
public sealed class RequestPolicyState
{
    private static readonly TimeSpan AdmissionInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ThreadInterval = TimeSpan.FromSeconds(10);
    private static readonly Regex ThreadEndpoint = new Regex(@"^/(?<board>[^/]+)/thread/(?<id>[0-9]+)\.json$", RegexOptions.CultureInvariant);
    private readonly object _stateLock = new object();
    private readonly Dictionary<Scope, HostState> _hosts = new Dictionary<Scope, HostState>();
    private readonly Func<double> _jitter;
    private readonly long _origin;

    public TimeProvider TimeProvider { get; }

    public RequestPolicyState(TimeProvider? timeProvider = null, Func<double>? jitter = null)
    {
        TimeProvider = timeProvider ?? System.TimeProvider.System;
        _origin = TimeProvider.GetTimestamp();
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    internal HostState GetHost(string source, Uri uri)
    {
        Scope scope = new Scope(source, uri.IdnHost.ToLowerInvariant());
        lock (_stateLock)
        {
            if (!_hosts.TryGetValue(scope, out HostState? host))
            {
                bool isApi = scope.Host == "a.4cdn.org" || scope.Host == "8kun.top";
                host = new HostState(isApi);
                _hosts.Add(scope, host);
            }
            return host;
        }
    }

    internal static string? GetThreadEndpoint(Uri uri)
    {
        if (!string.Equals(uri.IdnHost, "a.4cdn.org", StringComparison.OrdinalIgnoreCase)) return null;
        Match match = ThreadEndpoint.Match(uri.AbsolutePath);
        if (!match.Success) return null;
        string id = match.Groups["id"].Value.TrimStart('0');
        return match.Groups["board"].Value.ToLowerInvariant() + "/" + (id.Length == 0 ? "0" : id);
    }

    internal async Task<Task<HttpResponseMessage>> DispatchAsync(HostState host, string? threadEndpoint,
        Func<Task<HttpResponseMessage>> send, Action startBudget,
        Action<RequestPolicyPhase, TimeSpan> waiting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter = new Waiter();
        LinkedListNode<Waiter> node;
        lock (host.DispatchLock)
        {
            node = host.Pending.AddLast(waiter);
            if (ReferenceEquals(host.Pending.First, node)) waiter.Turn.TrySetResult();
        }
        try
        {
            await waiter.Turn.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                long remaining;
                bool cooldown;
                Task changed;
                lock (host.DispatchLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long now = NowTicks;
                    RemoveExpiredThreadAdmissions(host, now);
                    cooldown = host.CooldownUntil > now;
                    if (cooldown) startBudget();
                    long due = Math.Max(host.NextAdmission, host.CooldownUntil);
                    if (threadEndpoint != null && host.NextThreadAdmission.TryGetValue(threadEndpoint, out long threadDue))
                        due = Math.Max(due, threadDue);
                    remaining = due - now;
                    if (remaining <= 0)
                    {
                        startBudget();
                        cancellationToken.ThrowIfCancellationRequested();
                        // RecordFailure takes the same host-local lock. Deadline
                        // recheck and synchronous dispatch form one admission;
                        // no response await or observer runs under this lock.
                        try { return send(); }
                        finally
                        {
                            long started = NowTicks;
                            long spacing = host.IsApi || host.Recovering ? AdmissionInterval.Ticks : 0;
                            host.NextAdmission = AddTicks(started, spacing);
                            if (threadEndpoint != null)
                                host.NextThreadAdmission[threadEndpoint] = AddTicks(started, ThreadInterval.Ticks);
                            RemoveWaiterLocked(host, node);
                            if (host.Pending.Count == 0) host.Recovering = false;
                        }
                    }
                    changed = host.Changed.Task;
                }

                // Long server deadlines remain intact. A bounded individual delay
                // avoids timer range limits. A new cooldown wakes the head early
                // so it can start its bounded budget and report the changed wait.
                TimeSpan delay = TimeSpan.FromTicks(Math.Min(remaining, TimeSpan.TicksPerDay));
                waiting(cooldown ? RequestPolicyPhase.Cooldown : RequestPolicyPhase.WaitingForAdmission, delay);
                try { await changed.WaitAsync(delay, TimeProvider, cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        finally
        {
            lock (host.DispatchLock) { RemoveWaiterLocked(host, node); }
        }
    }

    internal void RecordFailure(HostState host, RetryConditionHeaderValue? retryAfter, int attempt)
    {
        TimeSpan delay;
        if (retryAfter?.Delta is TimeSpan delta && delta >= TimeSpan.Zero)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is DateTimeOffset date)
        {
            delay = date - TimeProvider.GetUtcNow();
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        }
        else
        {
            double jitter = _jitter();
            if (!double.IsFinite(jitter)) jitter = 0;
            double seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5)));
            delay = TimeSpan.FromSeconds(seconds * (1 + 0.2 * Math.Clamp(jitter, 0, 1)));
        }

        TaskCompletionSource changed;
        lock (host.DispatchLock)
        {
            host.CooldownUntil = Math.Max(host.CooldownUntil, AddTicks(NowTicks, delay.Ticks));
            host.Recovering = true;
            changed = host.Changed;
            host.Changed = CreateSignal();
        }
        changed.TrySetResult();
    }

    private long NowTicks => TimeProvider.GetElapsedTime(_origin, TimeProvider.GetTimestamp()).Ticks;

    private static void RemoveWaiterLocked(HostState host, LinkedListNode<Waiter> node)
    {
        if (node.List == null) return;
        bool wasHead = ReferenceEquals(host.Pending.First, node);
        host.Pending.Remove(node);
        if (wasHead) host.Pending.First?.Value.Turn.TrySetResult();
    }

    private static void RemoveExpiredThreadAdmissions(HostState host, long now)
    {
        List<string>? expired = null;
        foreach (KeyValuePair<string, long> admission in host.NextThreadAdmission)
        {
            if (admission.Value <= now) (expired ??= new List<string>()).Add(admission.Key);
        }
        if (expired != null)
            foreach (string endpoint in expired) host.NextThreadAdmission.Remove(endpoint);
    }

    private static TaskCompletionSource CreateSignal() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long AddTicks(long timestamp, long ticks) =>
        timestamp > long.MaxValue - ticks ? long.MaxValue : timestamp + ticks;

    private readonly record struct Scope(string Source, string Host);

    internal sealed class HostState
    {
        public object DispatchLock { get; } = new object();
        public LinkedList<Waiter> Pending { get; } = new LinkedList<Waiter>();
        public Dictionary<string, long> NextThreadAdmission { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public TaskCompletionSource Changed { get; set; } = CreateSignal();
        public bool IsApi { get; }
        public long NextAdmission { get; set; }
        public long CooldownUntil { get; set; }
        public bool Recovering { get; set; }

        public HostState(bool isApi) => IsApi = isApi;
    }

    internal sealed class Waiter
    {
        public TaskCompletionSource Turn { get; } = CreateSignal();
    }
}

/// <summary>
/// Applies shared admission, response-header deadlines and bounded retries to a
/// borrowed transport. Dispose never disposes or modifies the supplied HttpClient.
/// Successful response bodies remain the caller's responsibility, including their
/// read deadlines. The transport must honor cancellation and must not hide further
/// requests in its own retry or redirect policy. Its synchronous SendAsync entry
/// must be short and nonblocking because dispatch is serialized per host.
/// </summary>
public sealed class RequestPolicyHandler : HttpMessageHandler
{
    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(60);
    public const int MaximumAttempts = 3;
    internal static readonly HttpRequestOptionsKey<Action<RequestPolicyProgress>> ObserverKey = new HttpRequestOptionsKey<Action<RequestPolicyProgress>>("oChan.RequestPolicyObserver");

    private readonly HttpClient _transport;
    private readonly string _source;
    private readonly RequestPolicyState _policy;
    private readonly ILogger _logger;

    public RequestPolicyHandler(HttpClient transport, string source, RequestPolicyState policy, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(policy);
        _transport = transport;
        _source = source;
        _policy = policy;
        _logger = logger ?? Serilog.Core.Logger.None;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("An absolute request URI is required.");
        if (!uri.IsAbsoluteUri) throw new InvalidOperationException("An absolute request URI is required.");
        RequestPolicyState.HostState host = _policy.GetHost(_source, uri);
        bool replayable = request.Content == null && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);
        int attempts = replayable ? MaximumAttempts : 1;

        request.Options.TryGetValue(ObserverKey, out Action<RequestPolicyProgress>? observer);
        // A healthy initial rate queue can be arbitrarily long. The bounded budget
        // begins at the first send or when this FIFO head encounters server cooldown;
        // all subsequent admission, cooldown and header time uses that same budget.
        using RequestBudget budget = new RequestBudget(_policy.TimeProvider, cancellationToken, _logger);
        try
        {
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                budget.Token.ThrowIfCancellationRequested();
                HttpResponseMessage? response = null;
                Exception? failure = null;
                try
                {
                    response = await SendAttemptAsync(request, host, budget, observer, attempt).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) { failure = exception; }
                catch (TimeoutException exception) { failure = exception; }

                if (response != null && !IsTransient(response.StatusCode)) return response;

                _policy.RecordFailure(host, response?.Headers.RetryAfter, attempt);
                if (attempt == attempts)
                {
                    if (response != null) return response;
                    ExceptionDispatchInfo.Capture(failure!).Throw();
                }
                response?.Dispose();
            }
            throw new InvalidOperationException("Retry attempts were exhausted unexpectedly.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException exception) when (budget.Expired)
        {
            throw new TimeoutException("The HTTP request exceeded its 60-second network and cooldown budget.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage original,
        RequestPolicyState.HostState host, RequestBudget budget,
        Action<RequestPolicyProgress>? observer, int attemptNumber)
    {
        using HttpRequestMessage request = CloneRequest(original);
        CancellationTokenSource? headers = null;
        CancellationTokenSource? attempt = null;
        try
        {
            Notify(observer, new RequestPolicyProgress(RequestPolicyPhase.WaitingForAdmission, null, attemptNumber));
            try
            {
                Task<HttpResponseMessage> sending = await _policy.DispatchAsync(host,
                    RequestPolicyState.GetThreadEndpoint(original.RequestUri!), () =>
                    {
                        headers = new CancellationTokenSource(HeaderTimeout, _policy.TimeProvider);
                        attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, headers.Token);
                        return _transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    }, budget.Start,
                    (phase, delay) => Notify(observer, new RequestPolicyProgress(phase, delay, attemptNumber)),
                    budget.Token).ConfigureAwait(false);
                Notify(observer, new RequestPolicyProgress(RequestPolicyPhase.Sending, null, attemptNumber));
                HttpResponseMessage response = await sending.ConfigureAwait(false);
                if (budget.Token.IsCancellationRequested || headers!.IsCancellationRequested)
                {
                    response.Dispose();
                    // A source marks itself cancelled before its linked-token
                    // callbacks finish. Never rely on the linked token already
                    // reflecting that state after disposing this response.
                    throw new OperationCanceledException(attempt!.Token);
                }
                response.RequestMessage = original;
                return response;
            }
            catch (OperationCanceledException exception) when (!budget.Token.IsCancellationRequested)
            {
                throw new TimeoutException("The HTTP response headers did not arrive before the attempt deadline.", exception);
            }
        }
        finally
        {
            attempt?.Dispose();
            headers?.Dispose();
            // Only the message is cloned; a non-replayed body is borrowed from
            // the caller and must survive success, failure and cancelled admission.
            request.Content = null;
        }
    }

    private void Notify(Action<RequestPolicyProgress>? observer, RequestPolicyProgress progress)
    {
        try { observer?.Invoke(progress); }
        catch (Exception exception) { _logger.Warning(exception, "HTTP policy observer failed"); }
    }

    private sealed class RequestBudget : IDisposable
    {
        private readonly TimeProvider _clock;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _timeout = new CancellationTokenSource();
        private readonly CancellationTokenSource _operation;
        private ITimer? _timer;
        public CancellationToken Token => _operation.Token;
        public bool Expired => _timeout.IsCancellationRequested;

        public RequestBudget(TimeProvider clock, CancellationToken caller, ILogger logger)
        {
            _clock = clock;
            _logger = logger;
            _operation = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
        }

        public void Start()
        {
            if (_timer != null) return;
            // Create the timer only now: CancelAfter on a plain CTS would silently
            // use wall-clock time instead of the injected TimeProvider.
            _timer = _clock.CreateTimer(static state =>
            {
                RequestBudget budget = (RequestBudget)state!;
                try { budget._timeout.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (Exception exception) { budget._logger.Warning(exception, "HTTP deadline cancellation observer failed"); }
            }, this, OverallTimeout, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _operation.Dispose();
            _timeout.Dispose();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500 && (int)statusCode <= 599;

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        HttpRequestMessage copy = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
            Content = original.Content
        };
        foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (KeyValuePair<string, object?> option in original.Options)
            copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return copy;
    }
}

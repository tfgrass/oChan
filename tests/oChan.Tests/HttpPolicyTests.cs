using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using oChan.Http;

namespace oChan.Tests;

public sealed class HttpPolicyTests
{
    [Theory]
    [InlineData("4chan", "a.4cdn.org")]
    [InlineData("8kun", "8kun.top")]
    public async Task ConcurrentClientsShareApiAdmissionWithoutCatchupBursts(string source, string host)
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RequestPolicyState policy = new RequestPolicyState(clock, () => 0);
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient first = Client(transport, source, policy);
        using HttpClient second = Client(transport, source, policy);
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 4)
            .Select(index => (index % 2 == 0 ? first : second).GetAsync($"https://{host}/board{index}/catalog.json"))
            .ToArray();

        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await TestWait.UntilAsync(() => handler.Count == 2);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(2));

        // A late clock jump admits one request now; missed slots are not replayed.
        clock.Advance(TimeSpan.FromSeconds(4));
        await TestWait.UntilAsync(() => handler.Count == 3);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(3, handler.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        HttpResponseMessage[] responses = await Task.WhenAll(requests).WaitAsync(TestWait.Timeout);
        foreach (HttpResponseMessage response in responses) response.Dispose();

        Assert.Equal(new[] { 0d, 1d, 5d, 6d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Theory]
    [InlineData("4chan", "a.4cdn.org")]
    [InlineData("8kun", "8kun.top")]
    public async Task HealthyInitialQueueCanExceedSixtySecondsAndPreservesFifoOrder(string source, string host)
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, source, new RequestPolicyState(clock, () => 0));
        string[] urls = Enumerable.Range(0, 70).Select(index => $"https://{host}/board{index}/catalog.json").ToArray();
        Task<HttpResponseMessage>[] requests = urls.Select(url => client.GetAsync(url)).ToArray();

        Assert.Equal(1, handler.Count);
        for (int index = 0; index < requests.Length; index++)
        {
            if (index > 0)
            {
                await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(index));
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            using HttpResponseMessage response = await requests[index].WaitAsync(TestWait.Timeout);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(index + 1, handler.Count);
        }

        Assert.Equal(urls, handler.Requests.Select(request => request.RequestUri!.AbsoluteUri));
        Assert.Equal(Enumerable.Range(0, 70).Select(index => (double)index), handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task CancellingMiddleFifoWaiterPreservesOrderOfRemainingRequests()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        using HttpResponseMessage first = await client.GetAsync("https://a.4cdn.org/first/catalog.json");
        Task<HttpResponseMessage> second = client.GetAsync("https://a.4cdn.org/second/catalog.json");
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> cancelled = client.GetAsync("https://a.4cdn.org/cancelled/catalog.json", cancellation.Token);
        Task<HttpResponseMessage> fourth = client.GetAsync("https://a.4cdn.org/fourth/catalog.json");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(TestWait.Timeout));
        clock.Advance(TimeSpan.FromSeconds(1));
        using HttpResponseMessage secondResult = await second.WaitAsync(TestWait.Timeout);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        using HttpResponseMessage fourthResult = await fourth.WaitAsync(TestWait.Timeout);

        Assert.Equal(new[] { "/first/catalog.json", "/second/catalog.json", "/fourth/catalog.json" },
            handler.Requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.Equal(new[] { 0d, 1d, 2d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task RepeatedFourChanThreadEndpointIncludesQueryVariantsInTenSecondCadence()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        using HttpResponseMessage first = await client.GetAsync("https://a.4cdn.org/g/thread/000123.json?first=1");
        Task<HttpResponseMessage> second = client.GetAsync("https://a.4cdn.org/g/thread/123.json?second=2");
        clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using HttpResponseMessage result = await second.WaitAsync(TestWait.Timeout);
        Assert.Equal(new[] { 0d, 10d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task FourChanThreadRetriesRetainTenSecondEndpointCadence()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        ConcurrentQueue<TrackingContent> contents = new ConcurrentQueue<TrackingContent>();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) =>
        {
            TrackingContent content = new TrackingContent();
            contents.Enqueue(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content });
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> pending = client.GetAsync("https://a.4cdn.org/g/thread/123.json");
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        // At this point t20 could still be the second attempt's header timer.
        // Wait until that response was handled before looking for admission.
        await TestWait.UntilAsync(() => contents.Count == 2 && contents.Last().Disposed);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(10));
        using HttpResponseMessage response = await pending.WaitAsync(TestWait.Timeout);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new[] { 0d, 10d, 20d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task PreexistingServerCooldownStartsBoundedBudgetBeforeFirstSend()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        using HttpResponseMessage first = await client.PostAsync("https://i.4cdn.org/synthetic", null);
        ConcurrentQueue<RequestPolicyProgress> progress = new ConcurrentQueue<RequestPolicyProgress>();
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://i.4cdn.org/g/image.jpg");
        request.Options.Set(RequestPolicyHandler.ObserverKey, progress.Enqueue);
        Task<HttpResponseMessage> pending = client.SendAsync(request);

        clock.Advance(TimeSpan.FromMilliseconds(59999));
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TestWait.Timeout));
        Assert.Equal(1, handler.Count);
        Assert.Contains(progress, state => state.Phase == RequestPolicyPhase.Cooldown && state.Delay == TimeSpan.FromSeconds(120) && state.Attempt == 1);
        Assert.DoesNotContain(progress, state => state.Phase == RequestPolicyPhase.Sending);
    }

    [Fact]
    public async Task NewlyReportedCooldownWakesAdmissionHeadAndStartsItsBudgetImmediately()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        TaskCompletionSource<HttpResponseMessage> failure = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackingContent content = new TrackingContent();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => failure.Task);
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> first = client.GetAsync("https://a.4cdn.org/g/thread/123.json");
        Task<HttpResponseMessage> second = client.GetAsync("https://a.4cdn.org/g/thread/123.json");
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(2));
        HttpResponseMessage limited = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        failure.SetResult(limited);
        await TestWait.UntilAsync(() => content.Disposed);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(62));

        clock.Advance(TimeSpan.FromSeconds(58));
        await Assert.ThrowsAsync<TimeoutException>(async () => await first.WaitAsync(TestWait.Timeout));
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(async () => await second.WaitAsync(TestWait.Timeout));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task LaterRetryAfterExtendsDeadlineBeforeQueuedRequestsDispatch()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        TaskCompletionSource<HttpResponseMessage> firstFailure = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> secondFailure = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTransport handler = new RecordingTransport(clock, (_, count, _) => count switch
        {
            1 => firstFailure.Task,
            2 => secondFailure.Task,
            _ => Task.FromResult(Response(HttpStatusCode.OK))
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> first = client.GetAsync("https://i.4cdn.org/g/first.jpg");
        Task<HttpResponseMessage> second = client.GetAsync("https://i.4cdn.org/g/second.jpg");
        TrackingContent firstContent = new TrackingContent();
        HttpResponseMessage firstLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = firstContent };
        firstLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        firstFailure.SetResult(firstLimited);
        await TestWait.UntilAsync(() => firstContent.Disposed);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(5));
        Task<HttpResponseMessage> third = client.GetAsync("https://i.4cdn.org/g/third.jpg");

        clock.Advance(TimeSpan.FromSeconds(2));
        TrackingContent secondContent = new TrackingContent();
        HttpResponseMessage secondLimited = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = secondContent };
        secondLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(8));
        secondFailure.SetResult(secondLimited);
        await TestWait.UntilAsync(() => secondContent.Disposed);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(2, handler.Count);
        clock.Advance(TimeSpan.FromSeconds(5));
        using HttpResponseMessage firstResult = await first.WaitAsync(TestWait.Timeout);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(11));
        clock.Advance(TimeSpan.FromSeconds(1));
        using HttpResponseMessage thirdResult = await third.WaitAsync(TestWait.Timeout);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(12));
        clock.Advance(TimeSpan.FromSeconds(1));
        using HttpResponseMessage secondResult = await second.WaitAsync(TestWait.Timeout);

        Assert.Equal(new[] { 0d, 0d, 10d, 11d, 12d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task OrdinaryMediaRequestsHaveNoFixedCadence()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock));
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 8)
            .Select(index => client.GetAsync($"https://i.4cdn.org/g/{index}.jpg")).ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests).WaitAsync(TestWait.Timeout);
        foreach (HttpResponseMessage response in responses) response.Dispose();
        Assert.Equal(8, handler.Count);
        Assert.All(handler.Starts, start => Assert.Equal(TimeSpan.Zero, start));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterIsSharedAndMediaBacklogResumesWithoutBurst(bool useDate)
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RequestPolicyState policy = new RequestPolicyState(clock, () => 0);
        RecordingTransport handler = new RecordingTransport(clock, (_, count, _) =>
        {
            HttpResponseMessage response = Response(count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (count == 1)
                response.Headers.RetryAfter = useDate
                    ? new RetryConditionHeaderValue(clock.GetUtcNow() + TimeSpan.FromSeconds(5))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient first = Client(transport, "4chan", policy);
        using HttpClient second = Client(transport, "4chan", policy);
        Task<HttpResponseMessage>[] requests =
        [
            first.GetAsync("https://i.4cdn.org/g/limited.jpg"),
            second.GetAsync("https://i.4cdn.org/g/other.jpg"),
            second.GetAsync("https://i.4cdn.org/g/third.jpg")
        ];
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await TestWait.UntilAsync(() => handler.Count == 2);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(6));
        clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => handler.Count == 3);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(7));
        clock.Advance(TimeSpan.FromSeconds(1));
        HttpResponseMessage[] responses = await Task.WhenAll(requests).WaitAsync(TestWait.Timeout);
        foreach (HttpResponseMessage response in responses) response.Dispose();
        Assert.Equal(new[] { 0d, 5d, 6d, 7d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task RetryAfterBeyondOperationBudgetKeepsItsSharedDeadline()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, count, _) =>
        {
            HttpResponseMessage response = Response(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            if (count == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> expired = client.GetAsync("https://i.4cdn.org/g/first.jpg");
        clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<TimeoutException>(async () => await expired.WaitAsync(TestWait.Timeout));
        Assert.Equal(1, handler.Count);

        clock.Advance(TimeSpan.FromSeconds(40));
        Task<HttpResponseMessage>[] later = Enumerable.Range(0, 3)
            .Select(index => client.GetAsync($"https://i.4cdn.org/g/later{index}.jpg")).ToArray();
        clock.Advance(TimeSpan.FromMilliseconds(19999));
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await TestWait.UntilAsync(() => handler.Count == 2);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(121));
        clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => handler.Count == 3);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(122));
        clock.Advance(TimeSpan.FromSeconds(1));
        HttpResponseMessage[] responses = await Task.WhenAll(later).WaitAsync(TestWait.Timeout);
        foreach (HttpResponseMessage response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
        Assert.Equal(new[] { 0d, 120d, 121d, 122d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RetriesAreBoundedAndDisposeOnlyReplacedResponses(HttpStatusCode status)
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        List<TrackingContent> contents = new List<TrackingContent>();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) =>
        {
            TrackingContent content = new TrackingContent();
            lock (contents) { contents.Add(content); }
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://i.4cdn.org/g/image.jpg");
        request.Headers.IfModifiedSince = clock.GetUtcNow();
        request.Options.Set(new HttpRequestOptionsKey<string>("synthetic"), "retained");
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => handler.Count == 2);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(2));
        HttpResponseMessage result = await pending.WaitAsync(TestWait.Timeout);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(3, handler.Count);
        Assert.Equal(3, handler.Requests.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(handler.Requests, clone =>
        {
            Assert.Equal(request.Headers.IfModifiedSince, clone.Headers.IfModifiedSince);
            Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<string>("synthetic"), out string? option));
            Assert.Equal("retained", option);
        });
        Assert.True(contents[0].Disposed);
        Assert.True(contents[1].Disposed);
        Assert.False(contents[2].Disposed);
        result.Dispose();
        Assert.True(contents[2].Disposed);
    }

    [Fact]
    public async Task NetworkFailuresGetAtMostThreeAttempts()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Synthetic connection failure")));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "8kun", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> pending = client.GetAsync("https://media.128ducks.com/file_store/image.jpg");
        clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => handler.Count == 2);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pending.WaitAsync(TestWait.Timeout));
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task HeaderDeadlinesCancelAndDrainEachAttemptBeforeRetry()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        int active = 0;
        int cancelled = 0;
        RecordingTransport handler = new RecordingTransport(clock, async (_, _, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Synthetic request cannot complete normally.");
            }
            finally
            {
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref cancelled);
            }
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> pending = client.GetAsync("https://i.4cdn.org/g/image.jpg");
        clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.Equal(0, cancelled);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await TestWait.UntilAsync(() => Volatile.Read(ref cancelled) == 1);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(11));
        clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => handler.Count == 2);
        clock.Advance(TimeSpan.FromSeconds(10));
        await TestWait.UntilAsync(() => Volatile.Read(ref cancelled) == 2);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(23));
        clock.Advance(TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(() => handler.Count == 3);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TestWait.Timeout));
        Assert.Equal(3, handler.Count);
        Assert.Equal(3, cancelled);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task ResponseArrivingAfterHeaderDeadlineIsDisposedAndReportedAsTimeout()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        TaskCompletionSource<HttpResponseMessage> delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => delayed.Task);
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> pending = client.PostAsync("https://i.4cdn.org/synthetic", null);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);

        TrackingContent content = new TrackingContent();
        delayed.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TestWait.Timeout));
        Assert.True(content.Disposed);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("cooldown")]
    [InlineData("headers")]
    public async Task CancellationTerminatesEveryWaitingPhaseWithoutAnotherSend(string phase)
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, async (_, _, token) =>
        {
            if (phase == "headers") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(phase == "cooldown" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        string uri = phase == "admission" ? "https://a.4cdn.org/g/catalog.json" : "https://i.4cdn.org/g/image.jpg";
        if (phase == "admission")
        {
            using HttpResponseMessage first = await client.GetAsync(uri);
        }
        Task<HttpResponseMessage> pending = client.GetAsync(uri, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TestWait.Timeout));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task SuccessFromAnOlderRequestCannotClearSharedCooldown()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        TaskCompletionSource<HttpResponseMessage> olderResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTransport handler = new RecordingTransport(clock, (_, count, _) =>
        {
            if (count == 1) return olderResponse.Task;
            HttpResponseMessage response = Response(count == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            if (count == 2) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> older = client.GetAsync("https://i.4cdn.org/g/older.jpg");
        Task<HttpResponseMessage> retrying = client.GetAsync("https://i.4cdn.org/g/failing.jpg");
        olderResponse.SetResult(Response(HttpStatusCode.OK));
        using HttpResponseMessage oldResult = await older.WaitAsync(TestWait.Timeout);
        Task<HttpResponseMessage> waiting = client.GetAsync("https://i.4cdn.org/g/later.jpg");
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Equal(2, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await TestWait.UntilAsync(() => handler.Count == 3);
        await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(6));
        clock.Advance(TimeSpan.FromSeconds(1));
        using HttpResponseMessage retried = await retrying.WaitAsync(TestWait.Timeout);
        using HttpResponseMessage later = await waiting.WaitAsync(TestWait.Timeout);
        Assert.Equal(new[] { 0d, 0d, 5d, 6d }, handler.Starts.Select(start => start.TotalSeconds));
    }

    [Fact]
    public async Task NonReplayableRequestsAndNotModifiedResponsesAreReturnedDirectly()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (request, _, _) =>
            Task.FromResult(Response(request.Method == HttpMethod.Post ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotModified)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock));
        using HttpResponseMessage post = await client.PostAsync("https://first.invalid/action", new StringContent("body"));
        using HttpResponseMessage unchanged = await client.GetAsync("https://second.invalid/image");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerRequestContentSurvivesForwardingAndCancelledAdmission(bool cancelAdmission)
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)));
        using HttpClient transport = new HttpClient(handler);
        using HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock, () => 0));
        using TrackingContent content = new TrackingContent();
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://a.4cdn.org/synthetic") { Content = content };
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        if (cancelAdmission)
        {
            using HttpRequestMessage firstRequest = new HttpRequestMessage(HttpMethod.Post, "https://a.4cdn.org/first");
            using HttpResponseMessage first = await client.SendAsync(firstRequest);
        }
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancellation.Token);
        if (cancelAdmission)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TestWait.Timeout));
        }
        else
        {
            using HttpResponseMessage response = await pending.WaitAsync(TestWait.Timeout);
        }
        Assert.False(content.Disposed);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task DisposingPolicyClientLeavesBorrowedTransportUsable()
    {
        ManualPolicyTime clock = new ManualPolicyTime();
        RecordingTransport handler = new RecordingTransport(clock, (_, _, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        using HttpClient transport = new HttpClient(handler);
        TimeSpan originalTimeout = transport.Timeout;
        using (HttpClient client = Client(transport, "4chan", new RequestPolicyState(clock)))
        {
            using HttpResponseMessage response = await client.GetAsync("https://i.4cdn.org/g/image.jpg");
        }
        using HttpResponseMessage remaining = await transport.GetAsync("https://synthetic.invalid/direct");
        Assert.Equal(originalTimeout, transport.Timeout);
        Assert.Equal(2, handler.Count);
    }

    private static HttpClient Client(HttpClient transport, string source, RequestPolicyState policy) =>
        new HttpClient(new RequestPolicyHandler(transport, source, policy)) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Response(HttpStatusCode status) =>
        new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };

    private sealed class RecordingTransport(ManualPolicyTime clock,
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _count;
        private readonly ConcurrentQueue<TimeSpan> _starts = new ConcurrentQueue<TimeSpan>();
        private readonly ConcurrentQueue<HttpRequestMessage> _requests = new ConcurrentQueue<HttpRequestMessage>();
        public int Count => Volatile.Read(ref _count);
        public TimeSpan[] Starts => _starts.ToArray();
        public HttpRequestMessage[] Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _starts.Enqueue(TimeSpan.FromTicks(clock.GetTimestamp()));
            _requests.Enqueue(request);
            int count = Interlocked.Increment(ref _count);
            return response(request, count, cancellationToken);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ManualPolicyTime : TimeProvider
    {
        private readonly object _sync = new object();
        private readonly List<ManualTimer> _timers = new List<ManualTimer>();
        private long _ticks;
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.ChangeLocked(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToList();
                foreach (ManualTimer timer in due)
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + timer.Period.Ticks;
            }
            foreach (ManualTimer timer in due) timer.Callback(timer.State);
        }

        public Task WaitForTimerAtAsync(TimeSpan elapsed) => TestWait.UntilAsync(() =>
        {
            lock (_sync) { return _timers.Any(timer => !timer.Disposed && timer.Due == elapsed.Ticks); }
        }, $"No policy timer was scheduled for {elapsed}.");

        private sealed class ManualTimer(ManualPolicyTime owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (Disposed) return false;
                    ChangeLocked(dueTime, period);
                    return true;
                }
            }

            public void ChangeLocked(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period;
            }

            public void Dispose() { lock (owner._sync) { Disposed = true; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

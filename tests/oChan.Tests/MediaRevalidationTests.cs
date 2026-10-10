using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Storage;

namespace oChan.Tests;

/// <summary>
/// Exercises completed-media revalidation through the actual provider, session, queue and store.
/// Only HTTP responses and the interval between real provider checks are controlled by the test.
/// </summary>
public sealed class MediaRevalidationTests
{
    private static readonly byte[] InitialPayload = [1, 2, 3, 5, 8, 13, 21, 34];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedCompletedMediaIsDownloadedAgainWithoutRestartingTheThread(bool eightKun)
    {
        await using RevalidationScenario scenario = await RevalidationScenario.StartAsync(eightKun);
        IThread originalThread = scenario.Thread;
        File.Delete(scenario.CanonicalPath);

        await scenario.NextCheckAsync();

        Assert.Same(originalThread, scenario.Thread);
        Assert.Equal(2, scenario.Handler.MediaRequests);
        MediaCommitResult restored = await scenario.AssertVerifiedCompletionAsync(InitialPayload);
        Assert.Equal(scenario.CanonicalPath, restored.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameSizeTamperingIsRepairedAndTheChangedFileIsPreserved(bool eightKun)
    {
        await using RevalidationScenario scenario = await RevalidationScenario.StartAsync(eightKun);
        byte[] changed = InitialPayload.Reverse().ToArray();
        await File.WriteAllBytesAsync(scenario.CanonicalPath, changed);

        await scenario.NextCheckAsync();

        Assert.Equal(2, scenario.Handler.MediaRequests);
        MediaCommitResult restored = await scenario.AssertVerifiedCompletionAsync(InitialPayload);
        Assert.NotEqual(scenario.CanonicalPath, restored.Path);
        Assert.Equal(changed, await File.ReadAllBytesAsync(scenario.CanonicalPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdatedProviderMetadataForTheSameMediaIdInvalidatesTheOldCompletion(
        bool eightKun, bool changeSize)
    {
        await using RevalidationScenario scenario = await RevalidationScenario.StartAsync(eightKun);
        byte[] replacement = changeSize
            ? InitialPayload.Concat(new byte[] { 55, 89 }).ToArray()
            : InitialPayload.Reverse().ToArray();
        scenario.Handler.SetPayload(replacement);

        await scenario.NextCheckAsync();

        Assert.Equal(2, scenario.Handler.MediaRequests);
        MediaCommitResult restored = await scenario.AssertVerifiedCompletionAsync(replacement);
        Assert.NotEqual(scenario.CanonicalPath, restored.Path);
        Assert.Equal(InitialPayload, await File.ReadAllBytesAsync(scenario.CanonicalPath));
        Assert.Single(scenario.Thread.DownloadedMedia);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRepairClearsThePreviousCompletionAndALaterCheckCanRecover(bool eightKun)
    {
        await using RevalidationScenario scenario = await RevalidationScenario.StartAsync(eightKun);
        byte[] changed = InitialPayload.Reverse().ToArray();
        await File.WriteAllBytesAsync(scenario.CanonicalPath, changed);
        scenario.Handler.FailMedia = true;

        await scenario.NextCheckAsync();

        Assert.Equal(2, scenario.Handler.MediaRequests);
        Assert.False(scenario.Thread.IsMediaDownloaded(scenario.Handler.MediaId));
        Assert.Empty(scenario.Thread.DownloadedMedia);
        Assert.DoesNotContain("(100%)", scenario.Thread.Progress);
        Assert.NotEqual("Finished", scenario.Thread.Status);
        Assert.Equal(changed, await File.ReadAllBytesAsync(scenario.CanonicalPath));

        scenario.Handler.FailMedia = false;
        await scenario.NextCheckAsync();

        Assert.Equal(3, scenario.Handler.MediaRequests);
        await scenario.AssertVerifiedCompletionAsync(InitialPayload);
        Assert.Equal(changed, await File.ReadAllBytesAsync(scenario.CanonicalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotModifiedMetadataStillRetriesAMediumWhoseFirstDownloadFailed(bool eightKun)
    {
        await using RevalidationScenario scenario = await RevalidationScenario.StartAsync(
            eightKun, failInitialMedia: true, notModifiedAfterFirst: true);
        Assert.False(scenario.Thread.IsMediaDownloaded(scenario.Handler.MediaId));
        Assert.False(File.Exists(scenario.CanonicalPath));
        scenario.Handler.FailMedia = false;

        await scenario.NextCheckAsync();

        Assert.Equal(2, scenario.Handler.ThreadRequests);
        Assert.Equal(1, scenario.Handler.ConditionalThreadRequests);
        Assert.Equal(2, scenario.Handler.MediaRequests);
        await scenario.AssertVerifiedCompletionAsync(InitialPayload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotModifiedMetadataStillDetectsADeletedCompletedFile(bool eightKun)
    {
        await using RevalidationScenario scenario = await RevalidationScenario.StartAsync(
            eightKun, notModifiedAfterFirst: true);
        File.Delete(scenario.CanonicalPath);

        await scenario.NextCheckAsync();

        Assert.Equal(2, scenario.Handler.ThreadRequests);
        Assert.Equal(1, scenario.Handler.ConditionalThreadRequests);
        Assert.Equal(2, scenario.Handler.MediaRequests);
        await scenario.AssertVerifiedCompletionAsync(InitialPayload);
    }

    private sealed class RevalidationScenario : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new TempDirectory();
        private readonly HttpClient _client;
        private readonly IImageBoard _provider;
        private readonly DownloadQueue _queue;
        private readonly Registry _registry;
        private AsyncStepGate _clock = null!;

        private RevalidationScenario(bool eightKun, bool failInitialMedia, bool notModifiedAfterFirst)
        {
            Handler = new RevalidationHandler(eightKun, InitialPayload, notModifiedAfterFirst)
            {
                FailMedia = failInitialMedia
            };
            _client = new HttpClient(Handler);
            _provider = eightKun ? new ControlledEightKunProvider(_client) : new ControlledFourChanProvider(_client);
            _queue = new DownloadQueue(1, 1L << 30, new MediaStore());
            _registry = new Registry(new IImageBoard[] { _provider }, _queue, _directory.Path,
                TimeSpan.FromHours(1), 3600);
        }

        public RevalidationHandler Handler { get; }
        public IThread Thread { get; private set; } = null!;
        public string CanonicalPath => Path.Combine(Thread.DownloadDirectory, Handler.MediaId + ".jpg");

        public static async Task<RevalidationScenario> StartAsync(bool eightKun,
            bool failInitialMedia = false, bool notModifiedAfterFirst = false)
        {
            RevalidationScenario scenario = new RevalidationScenario(eightKun, failInitialMedia, notModifiedAfterFirst);
            try
            {
                string url = eightKun ? "https://8kun.top/g/res/123.html" : "https://boards.4chan.org/g/thread/123";
                AdmissionResult added = await scenario._registry.AddAsync(url);
                Assert.Equal(AdmissionStatus.Added, added.Status);
                scenario.Thread = Assert.IsAssignableFrom<IThread>(added.Thread);
                scenario._clock = Assert.IsAssignableFrom<IControlledPolling>(scenario.Thread).Clock;
                await TestWait.UntilAsync(() => scenario._clock.Waits == 1,
                    "The initial real provider check did not reach its controlled polling interval.");
                await scenario._queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
                Assert.Equal(1, scenario.Handler.MediaRequests);
                if (!failInitialMedia) await scenario.AssertVerifiedCompletionAsync(InitialPayload);
                return scenario;
            }
            catch
            {
                await scenario.DisposeAsync();
                throw;
            }
        }

        public async Task NextCheckAsync()
        {
            int expectedWaits = _clock.Waits + 1;
            _clock.Advance();
            await TestWait.UntilAsync(() => _clock.Waits == expectedWaits,
                "The next real provider check did not complete.");
            await _queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        }

        public async Task<MediaCommitResult> AssertVerifiedCompletionAsync(byte[] expectedPayload)
        {
            DownloadItem item = new DownloadItem(Handler.MediaUri, CanonicalPath, _provider, Thread,
                Handler.MediaId, expectedSize: expectedPayload.Length,
                expectedMd5: Convert.ToBase64String(MD5.HashData(expectedPayload)));
            MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
            Assert.NotNull(restored);
            Assert.Equal(expectedPayload, await File.ReadAllBytesAsync(restored.Path));
            Assert.True(Thread.IsMediaDownloaded(Handler.MediaId));
            Assert.Single(Thread.DownloadedMedia);
            Assert.Contains("(100%)", Thread.Progress);
            return restored;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _registry.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout);
            }
            finally
            {
                _client.Dispose();
                _directory.Dispose();
            }
        }
    }

    private interface IControlledPolling
    {
        AsyncStepGate Clock { get; }
    }

    private sealed class ControlledFourChanProvider(HttpClient client) : FourChanImageBoard(client)
    {
        public override IThread GetThread(Uri threadUri)
        {
            IThread parsed = base.GetThread(threadUri);
            return new ControlledFourChanThread(parsed.Board, parsed.ThreadUri);
        }
    }

    private sealed class ControlledEightKunProvider(HttpClient client) : EightKunImageBoard(client)
    {
        public override IThread GetThread(Uri threadUri)
        {
            IThread parsed = base.GetThread(threadUri);
            return new ControlledEightKunThread(parsed.Board, parsed.ThreadUri);
        }
    }

    private sealed class ControlledFourChanThread(IBoard board, Uri uri) : FourChanThread(board, uri), IControlledPolling
    {
        public AsyncStepGate Clock { get; } = new AsyncStepGate();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
            => Clock.WaitAsync(cancellationToken);
    }

    private sealed class ControlledEightKunThread(IBoard board, Uri uri) : EightKunThread(board, uri), IControlledPolling
    {
        public AsyncStepGate Clock { get; } = new AsyncStepGate();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
            => Clock.WaitAsync(cancellationToken);
    }

    private sealed class RevalidationHandler : HttpMessageHandler
    {
        private static readonly DateTimeOffset LastModified = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        private readonly bool _eightKun;
        private readonly bool _notModifiedAfterFirst;
        private byte[] _payload;
        private int _failMedia;
        private int _mediaRequests;
        private int _threadRequests;
        private int _conditionalThreadRequests;

        public RevalidationHandler(bool eightKun, byte[] payload, bool notModifiedAfterFirst)
        {
            _eightKun = eightKun;
            _payload = payload.ToArray();
            _notModifiedAfterFirst = notModifiedAfterFirst;
            MediaId = eightKun ? new string('a', 64) : "1700000000001";
            MediaUri = new Uri(eightKun
                ? "https://nerv.8kun.top/file_store/" + MediaId + ".jpg"
                : "https://i.4cdn.org/g/" + MediaId + ".jpg");
        }

        public string MediaId { get; }
        public Uri MediaUri { get; }
        public int MediaRequests => Volatile.Read(ref _mediaRequests);
        public int ThreadRequests => Volatile.Read(ref _threadRequests);
        public int ConditionalThreadRequests => Volatile.Read(ref _conditionalThreadRequests);
        public bool FailMedia
        {
            get => Volatile.Read(ref _failMedia) != 0;
            set => Volatile.Write(ref _failMedia, value ? 1 : 0);
        }

        public void SetPayload(byte[] payload) => Volatile.Write(ref _payload, payload.ToArray());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("The synthetic HTTP request needs a URI.");
            if (uri.AbsolutePath == "/boards.json")
            {
                string boards = _eightKun
                    ? "[{\"uri\":\"g\",\"title\":\"Synthetic board\"}]"
                    : "{\"boards\":[{\"board\":\"g\",\"title\":\"Synthetic board\"}]}";
                return Task.FromResult(JsonResponse(boards));
            }

            if (uri.AbsolutePath is "/g/thread/123.json" or "/g/res/123.json")
            {
                int attempt = Interlocked.Increment(ref _threadRequests);
                if (request.Headers.IfModifiedSince == LastModified)
                    Interlocked.Increment(ref _conditionalThreadRequests);
                if (_notModifiedAfterFirst && attempt > 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));

                byte[] payload = Volatile.Read(ref _payload);
                object mediaId = _eightKun ? MediaId : long.Parse(MediaId, CultureInfo.InvariantCulture);
                Dictionary<string, object> post = new Dictionary<string, object>
                {
                    ["no"] = 123L,
                    ["sub"] = "Synthetic revalidation thread",
                    ["tim"] = mediaId,
                    ["ext"] = ".jpg",
                    ["filename"] = "synthetic",
                    ["fsize"] = payload.Length,
                    ["md5"] = Convert.ToBase64String(MD5.HashData(payload))
                };
                string json = JsonSerializer.Serialize(new { posts = new[] { post } });
                HttpResponseMessage response = JsonResponse(json);
                response.Content.Headers.LastModified = LastModified;
                return Task.FromResult(response);
            }

            if (uri == MediaUri)
            {
                Interlocked.Increment(ref _mediaRequests);
                return Task.FromResult(FailMedia
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Volatile.Read(ref _payload))
                    });
            }

            throw new InvalidOperationException("Unexpected synthetic request: " + uri.AbsoluteUri);
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}

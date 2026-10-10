using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using oChan;
using oChan.Boards.Base;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Storage;

namespace LibOChan.Consumer;

internal static class Program
{
    private static async Task Main()
    {
        string directory = Directory.CreateTempSubdirectory("libochan-consumer-").FullName;
        using DemoHandler handler = new DemoHandler();
        using HttpClient transport = new HttpClient(handler);

        try
        {
            // New providers, queues, stores and sessions share only the destination.
            // The second session must recover the committed medium without HTTP.
            await RunSessionAsync(transport, handler, directory, run: 1);
            await RunSessionAsync(transport, handler, directory, run: 2);
            if (handler.RequestCount != 1)
                throw new InvalidOperationException("Restoring verified media issued another request.");

            Console.WriteLine("Package consumer passed: verified download, manifest restore, and clean shutdown.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunSessionAsync(HttpClient transport, DemoHandler handler, string directory, int run)
    {
        DemoProvider provider = new DemoProvider(transport);
        MediaStore store = new MediaStore();
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024, store);
        await using Registry session = new Registry(
            new IImageBoard[] { provider }, queue, directory,
            TimeSpan.FromHours(1), boardIntervalSeconds: 3600, persistSession: false);

        AdmissionResult admission = await session.AddAsync("https://demo.invalid/demo/thread/1");
        if (admission.Status != AdmissionStatus.Added || admission.Thread is not DemoThread thread)
            throw new InvalidOperationException("The custom provider's thread was not admitted.");

        // AddAsync starts monitoring. The queue may still be idle until its first poll.
        await thread.FirstPollEnqueued.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string destination = Path.Combine(thread.DownloadDirectory, "1.bin");
        byte[] saved = await File.ReadAllBytesAsync(destination);
        if (!saved.AsSpan().SequenceEqual(DemoPayload.Bytes) || !thread.IsMediaDownloaded("1"))
            throw new InvalidOperationException("The download did not produce verified media.");
        ThreadSnapshot snapshot = session.GetSnapshot().Threads.Single();
        if (snapshot.KnownMedia != 1 || snapshot.CompletedMedia != 1 || snapshot.MediaFailures.Count != 0
            || snapshot.Downloads.Queued != 0 || snapshot.Downloads.Active != 0)
            throw new InvalidOperationException("The exposed progress does not match the saved medium.");

        ConsumerProgress progress = new ConsumerProgress(run, snapshot, saved.Length, handler.RequestCount);
        JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(progress, jsonOptions));

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (session.Threads.Count != 0 || session.Boards.Count != 0)
            throw new InvalidOperationException("Stopped watches remained active.");
    }

    private sealed record ConsumerProgress(int Run, ThreadSnapshot Thread, int SavedBytes, int MediaRequests);
}

internal static class DemoPayload
{
    public static readonly byte[] Bytes = Encoding.UTF8.GetBytes("libochan standalone consumer\n");
    public static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));
}

internal sealed class DemoHandler : HttpMessageHandler
{
    private int _requests;
    public int RequestCount => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://demo.invalid/media/1.bin")
            throw new InvalidOperationException("The offline example received an unexpected HTTP request.");

        Interlocked.Increment(ref _requests);
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(DemoPayload.Bytes)
        };
        return Task.FromResult(response);
    }
}

internal sealed class DemoProvider : BaseImageBoard
{
    public DemoProvider(HttpClient transport) : base(transport) { }
    public override string Name => "demo";
    public override string NiceName => "Offline example";
    public override Uri BaseUri => new Uri("https://demo.invalid/");
    public override bool CanHandle(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.Host == "demo.invalid" && string.IsNullOrEmpty(uri.UserInfo);
    public override bool IsThreadUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/demo/thread/1";
    public override bool IsBoardUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/demo/";
    public override IBoard GetBoard(Uri uri) => new DemoBoard(this);
    public override IThread GetThread(Uri uri) => new DemoThread(new DemoBoard(this));
}

internal sealed class DemoBoard : BaseBoard
{
    public DemoBoard(IImageBoard provider) => ImageBoard = provider;
    public override IImageBoard ImageBoard { get; }
    public override string BoardCode => "demo";
    public override string Name { get; protected set; } = "Demo board";
    public override string NiceName => Name;
    public override Uri BoardUri => new Uri(ImageBoard.BaseUri, "/demo/");

    public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<IThread> threads = new IThread[] { new DemoThread(this) };
        return Task.FromResult(threads);
    }
}

internal sealed class DemoThread : BaseThread
{
    private readonly TaskCompletionSource _firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public DemoThread(IBoard board) => Board = board;
    public Task FirstPollEnqueued => _firstPoll.Task;
    public override IBoard Board { get; }
    public override string ThreadId => "1";
    public override string NiceName => "Demo thread";
    public override Uri ThreadUri => new Uri(Board.ImageBoard.BaseUri, "/demo/thread/1");

    public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Title = "Package-only custom provider";
            TotalMediaCount = 1;
            Activity = ThreadActivity.Downloading;
            string path = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
                Board.BoardCode, ThreadId, "1", ".bin");
            DownloadItem item = new DownloadItem(new Uri("https://demo.invalid/media/1.bin"), path,
                Board.ImageBoard, this, "1", expectedSize: DemoPayload.Bytes.Length,
                expectedMd5: DemoPayload.Md5, originalFileName: "sample.bin");
            if (!queue.TryEnqueue(item))
                throw new InvalidOperationException("The demo media was not admitted to the queue.");

            _firstPoll.TrySetResult();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _firstPoll.TrySetException(exception);
            throw;
        }
    }
}

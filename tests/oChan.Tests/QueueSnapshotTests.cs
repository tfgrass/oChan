using System.Net;
using System.Threading.Channels;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.Tests;

public sealed class QueueSnapshotTests
{
    private const long UnthrottledBandwidth = 1L << 30;

    [Fact]
    public async Task CountsSeparateActiveAndQueuedJobsForEachRequestedThread()
    {
        using TempDirectory directory = new TempDirectory();
        HeldMediaHandler handler = new HeldMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(2, UnthrottledBandwidth);
        IThread first = board.GetThread(new Uri("https://stub.test/g/thread/1"));
        IThread second = board.GetThread(new Uri("https://stub.test/g/thread/2"));
        IThread idle = board.GetThread(new Uri("https://stub.test/g/thread/3"));

        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, first, directory, 1)));
            Assert.True(queue.TryEnqueue(CreateItem(board, second, directory, 1)));
            await handler.WaitForStartsAsync(2);
            foreach (IThread thread in new IThread[] { first, second })
            {
                Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 2)));
                Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 3)));
            }

            IReadOnlyDictionary<IThread, DownloadCounts> snapshot = queue.GetThreadCounts(
                new IThread[] { first, second, idle, first });

            Assert.Equal(3, snapshot.Count);
            Assert.Equal(new DownloadCounts(2, 1), snapshot[first]);
            Assert.Equal(new DownloadCounts(2, 1), snapshot[second]);
            Assert.Equal(new DownloadCounts(0, 0), snapshot[idle]);
            Assert.Equal(2, handler.RequestCount);

            Assert.True(queue.TryEnqueue(CreateItem(board, first, directory, 4)));
            IReadOnlyDictionary<IThread, DownloadCounts> updated = queue.GetThreadCounts(new IThread[] { first, second });
            Assert.Equal(new DownloadCounts(3, 1), updated[first]);
            Assert.Equal(new DownloadCounts(2, 1), updated[second]);

            IReadOnlyDictionary<IThread, DownloadCounts> subset = queue.GetThreadCounts(new IThread[] { first });
            Assert.Single(subset);
            Assert.Equal(new DownloadCounts(3, 1), subset[first]);
            Assert.False(subset.ContainsKey(second));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ReplacementInstanceDoesNotInheritItsPredecessorsCounts()
    {
        using TempDirectory directory = new TempDirectory();
        HeldMediaHandler handler = new HeldMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread original = new CanonicallyEqualThread(board.Board, new Uri("https://stub.test/g/thread/1"));
        IThread replacement = new CanonicallyEqualThread(board.Board, new Uri("https://stub.test/g/thread/1"));

        try
        {
            Assert.NotSame(original, replacement);
            Assert.True(original.Equals(replacement));
            Assert.True(queue.TryEnqueue(CreateItem(board, original, directory, 1)));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, original, directory, 2)));
            Assert.True(queue.TryEnqueue(CreateItem(board, original, directory, 3)));

            IReadOnlyDictionary<IThread, DownloadCounts> snapshot = queue.GetThreadCounts(
                new IThread[] { original, replacement });

            Assert.Equal(2, snapshot.Count);
            Assert.Equal(new DownloadCounts(2, 1), snapshot[original]);
            Assert.Equal(new DownloadCounts(0, 0), snapshot[replacement]);

            IReadOnlyDictionary<IThread, DownloadCounts> replacementOnly = queue.GetThreadCounts(
                new IThread[] { replacement });
            Assert.Single(replacementOnly);
            Assert.Equal(new DownloadCounts(0, 0), replacementOnly[replacement]);
            Assert.False(replacementOnly.ContainsKey(original));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task CancellationClearsCurrentCountsWithoutChangingEarlierSnapshots()
    {
        using TempDirectory directory = new TempDirectory();
        HeldMediaHandler handler = new HeldMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/1"));

        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 1)));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 2)));
            Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 3)));
            IReadOnlyDictionary<IThread, DownloadCounts> before = queue.GetThreadCounts(new IThread[] { thread });
            Assert.Equal(new DownloadCounts(2, 1), before[thread]);

            await queue.CancelDownloadsForThreadAsync(thread).WaitAsync(TestWait.Timeout);
            IReadOnlyDictionary<IThread, DownloadCounts> after = queue.GetThreadCounts(new IThread[] { thread });

            Assert.Equal(new DownloadCounts(0, 0), after[thread]);
            Assert.Equal(new DownloadCounts(2, 1), before[thread]);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(1, handler.CancellationCount);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task SnapshotMapAndCopiedCountsCannotMutateQueueState()
    {
        using TempDirectory directory = new TempDirectory();
        HeldMediaHandler handler = new HeldMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/1"));

        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 1)));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 2)));
            IReadOnlyDictionary<IThread, DownloadCounts> snapshot = queue.GetThreadCounts(new IThread[] { thread });

            DownloadCounts alteredCopy = snapshot[thread] with { Queued = 999, Active = 999 };
            Assert.Equal(new DownloadCounts(999, 999), alteredCopy);
            Assert.Equal(new DownloadCounts(1, 1), snapshot[thread]);

            // A snapshot may expose a dictionary compatibility interface, but it must remain read-only.
            if (snapshot is IDictionary<IThread, DownloadCounts> dictionary)
            {
                Assert.True(dictionary.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => { dictionary[thread] = alteredCopy; });
                Assert.Throws<NotSupportedException>(() => dictionary.Clear());
            }

            Assert.Equal(new DownloadCounts(1, 1), snapshot[thread]);
            Assert.Equal(new DownloadCounts(1, 1), queue.GetThreadCounts(new IThread[] { thread })[thread]);

            Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, 3)));
            Assert.Equal(new DownloadCounts(1, 1), snapshot[thread]);
            Assert.Equal(new DownloadCounts(2, 1), queue.GetThreadCounts(new IThread[] { thread })[thread]);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private static DownloadItem CreateItem(StubImageBoard board, IThread thread, TempDirectory directory,
        int mediaNumber)
        => new DownloadItem(new Uri($"https://stub.test/media/{thread.ThreadId}/{mediaNumber}.bin"),
            Path.Combine(directory.Path, thread.ThreadId, $"{mediaNumber}.bin"), board, thread, $"media-{mediaNumber}");

    private sealed class CanonicallyEqualThread(IBoard board, Uri uri) : ProbeThread(board, uri)
    {
        public override bool Equals(object? other)
            => other is CanonicallyEqualThread thread &&
               Board.ImageBoard.Name == thread.Board.ImageBoard.Name &&
               Board.BoardCode == thread.Board.BoardCode && ThreadId == thread.ThreadId;

        public override int GetHashCode() => HashCode.Combine(Board.ImageBoard.Name, Board.BoardCode, ThreadId);
    }

    private sealed class HeldMediaHandler : HttpMessageHandler
    {
        private readonly Channel<bool> _starts = Channel.CreateUnbounded<bool>();
        private readonly SemaphoreSlim _responses = new SemaphoreSlim(0);
        private int _requestCount;
        private int _cancellationCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public async Task WaitForStartsAsync(int count)
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TestWait.Timeout);
            for (int index = 0; index < count; index++)
                await _starts.Reader.ReadAsync(timeout.Token);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _starts.Writer.TryWrite(true);
            try
            {
                await _responses.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _responses.Dispose();
            base.Dispose(disposing);
        }
    }
}

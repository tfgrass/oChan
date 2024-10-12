# Downloader System Documentation

The downloader system in oChan consists of several classes that manage downloading media from imageboards. The primary classes involved are:

- `DownloadQueue`
- `DownloadWorker`
- `BandwidthLimiter`
- `DownloadItem`

## DownloadQueue Class

The `DownloadQueue` manages the queue of download tasks, controlling how many tasks run in parallel and how much bandwidth each task uses.

### Properties:
- **MaxParallelDownloads**: Maximum number of simultaneous downloads.
- **MaxBandwidthBytesPerSecond**: Maximum bandwidth allocation for downloads.

### Methods:

#### `EnqueueDownload(DownloadItem item)`
Adds a new `DownloadItem` to the queue for downloading.

- **Parameters**: 
  - `item`: An instance of `DownloadItem`.

#### `StopAll()`
Stops all downloads and cancels running tasks.

#### `UpdateMaxParallelDownloads(int newMax)`
Updates the maximum number of parallel downloads.

#### `UpdateMaxBandwidth(long newMaxBytesPerSecond)`
Updates the bandwidth limit for downloads.

## BandwidthLimiter Class

The `BandwidthLimiter` ensures that the download speed doesn't exceed the allowed limit.

### Methods:

#### `ThrottleAsync(int bytesDownloaded, CancellationToken cancellationToken)`
Limits the download speed based on the configured bandwidth limit.

## Example

```csharp
var downloadQueue = new DownloadQueue(3, 1024 * 1024); // 3 parallel downloads, 1MB/s bandwidth limit
var downloadItem = new DownloadItem(new Uri("https://i.4cdn.org/g/12345.jpg"), "Downloads/12345.jpg", imageBoard);
downloadQueue.EnqueueDownload(downloadItem);
```

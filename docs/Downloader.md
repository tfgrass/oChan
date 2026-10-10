# Download pipeline

Normal consumers admit URLs through `Registry`. Providers discover media descriptors, and the session's shared `DownloadQueue` schedules the work. The queue limits active media globally across watched boards and threads.

A job is reserved by source, board, thread, media ID and absolute destination. Different destinations are distinct jobs; duplicate descriptors for the same target do not create concurrent writes. A stopped watch cannot enqueue late work into its old lifetime.

## Completion and storage

The worker first checks whether stored media can be verified and reused. Otherwise it reads the HTTP body with cancellation and a bounded network-idle deadline. The store writes a temporary file, checks expected size/hash when provided, publishes the file atomically, and commits its manifest. Only then is the medium marked complete.

A pre-existing filename alone is not proof of completion. A new store verifies saved bytes before reuse. A running store can reuse verification of unchanged file fingerprints for up to ten minutes; an external edit preserving every fingerprint can defer detection until that deadline. Unrelated files are preserved; ambiguous legacy files are not silently adopted. Provider URLs can change without changing the stable media identity. Interrupted transfers are retried as complete downloads; byte-range resume is not implemented.

A missing medium does not remove its parent thread. The public `ThreadSnapshot` exposes source availability, typed activity/failures and actual queued/active media separately, so a source becoming archived or unavailable does not imply that every known medium was saved.

## Queue APIs

| API | Meaning |
| --- | --- |
| `TryEnqueue(DownloadItem)` | Reserves a job if its lifetime/identity can be admitted. Acceptance is not completion. |
| `WaitForIdleAsync()` | Waits until currently admitted media work has finished or been cancelled. Source monitoring can later add work. |
| `CancelDownloadsForThreadAsync(IThread)` | Cancels and awaits media belonging to that watch instance. |
| `StopAllAsync()` | Cancels and awaits the queue. Use session `StopAsync` to stop source polling as well. |
| `UpdateMaxParallelDownloads(int)` | Changes the global concurrency limit. |
| `UpdateMaxBandwidth(long)` | Changes the shared byte budget, in bytes per second. |
| `GetThreadCounts(threads)` | Returns queued/active counts for the requested watch instances. |

The synchronous stop/cancel adapters request cancellation; use the async APIs when completion is required. `DownloadWorker` and `MediaStore` are pipeline components; hosts should share the queue/store ownership rather than construct competing writers for one target.

## Custom provider descriptors

A custom provider supplies a `DownloadItem` with its resolved provider and thread objects:

```csharp
DownloadItem item = new DownloadItem(
    downloadUri: mediaUri,
    destinationPath: absoluteDestination,
    imageBoard: provider,
    thread: thread,
    mediaIdentifier: mediaId,
    expectedSize: expectedSize,
    expectedMd5: base64Md5,
    originalFileName: originalName);
bool admitted = queue.TryEnqueue(item);
```

The variables come from the provider's parsed response; the MD5 value is the provider's Base64 encoding, not hexadecimal. Keep source/board/thread/media identifiers stable, validate paths through the storage layout, and propagate cancellation while producing descriptors. Never mark a file complete merely because enqueue returned `true`.

The [session documentation](Registry.md) describes cancellation and persistence ownership. The package consumer sample provides a runnable custom provider.

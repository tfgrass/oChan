# Sessions and providers

`oChan.Registry` owns a session's watched boards and threads, their monitoring lifetimes, and one shared `DownloadQueue`. Constructing a registry or resolving a provider URL performs no HTTP requests. `AddAsync` starts monitoring after admission succeeds.

Create a queue with the desired global limits, register provider instances explicitly, and supply an absolute output root:

```csharp
using oChan;
using oChan.Boards.FourChan;
using oChan.Boards.EightKun;
using oChan.Downloader;
using oChan.Interfaces;

string threadOrBoardUrl = args[0];
string outputRoot = Path.GetFullPath("downloads");
IImageBoard[] providers = [new FourChanImageBoard(), new EightKunImageBoard()];
DownloadQueue queue = new DownloadQueue(5, 1024 * 1024);
await using Registry session = new Registry(
    providers, queue, outputRoot, TimeSpan.FromSeconds(60), 60, persistSession: false);

AdmissionResult result = await session.AddAsync(threadOrBoardUrl);
Console.WriteLine(result.Status);
Console.ReadLine();
await session.StopAsync();
```

The example watches the URL supplied as the first command-line argument until Enter is pressed. Keep the session alive for the application's desired monitoring lifetime. `DisposeAsync` or `StopAsync` cancels and awaits owned monitoring and downloads. `WaitForIdleAsync` on the queue only describes currently admitted media work; it does not mean that watched sources have stopped polling.

## Admission and removal

| API or result | Behavior |
| --- | --- |
| `AddAsync(url)` | Admits a canonical board or thread identity once. |
| `AdmissionStatus.Added` | A new watch was accepted. Media completion happens later. |
| `AdmissionStatus.AlreadyPresent` | The canonical resource already belongs to this session. |
| `AdmissionStatus.Invalid` | The URL is malformed or is not a recognized resource shape. |
| `AdmissionStatus.Unsupported` | No registered provider accepts the source. |
| `RemoveBoardAsync(board)` | Stops that board's discovery; existing thread watches continue. |
| `RemoveThreadAsync(thread)` | Cancels and awaits that watch and prevents automatic rediscovery. Explicit `AddAsync` clears the suppression. |
| `StopAsync()` | Stops runtime work while preserving saved watch intent for the next startup. |

Use the exact objects returned by admission or `GetView()` when removing resources. `RegistryView` pairs each `IThread`/`IBoard` handle with its copied snapshot from the same membership capture; this avoids joining a new lifetime to an old row by URL or ID. `Threads` and `Boards` also return copied membership lists. Re-adding a resource waits for its previous lifetime to finish. `IThread.DownloadDirectory` and `Registry.GetDownloadDirectory(board)` return assigned destinations, including after later configuration changes.

## Persistence ownership

Persistence is optional. A host that uses the file store acquires a profile lease, constructs `SessionStore`, passes it to `Registry`, and calls `RestoreAsync` before admitting new persistent watches:

```csharp
using oChan.Persistence;

string profileRoot = Path.GetFullPath("profile");
using ProfileLease lease = ProfileLease.Acquire(profileRoot);
SessionStore store = new SessionStore(profileRoot, lease);
await using Registry session = new Registry(
    providers, queue, outputRoot, TimeSpan.FromSeconds(60), 60,
    sessionStore: store, persistSession: true);
await session.RestoreAsync();
```

The lease must outlive the session; disposing in the order above satisfies that requirement. Enabled persistence saves confirmed additions, removals, suppression and assigned roots before acknowledging success. File-store errors propagate; the caller must display or otherwise handle them. `GetSnapshot().Warnings` retains the latest 32 recoverable session warnings. Unresolved saved URLs are retained for later recovery instead of silently discarded.

`ApplyOptionsAsync(SessionOptions, persistSession)` updates the queue limits and future polling delays. Existing watches keep their assigned destination. Directly added watches use the current root; discoveries from an existing board inherit that board's assigned root. Hosts should treat a settings update as complete only after the task succeeds. The desktop client's settings-file transaction is host behavior, not a requirement to use its `Config` type.

## Read state and observe changes

`GetSnapshot()` returns a `DownloadSessionSnapshot` with copied thread/board membership and resource state. The snapshot includes canonical `ResourceId` values, source availability, typed `ThreadActivity`, numeric known/completed media, actual queued/active counts, structured `FailureInfo` values, HTTP admission/cooldown progress, assigned directories and the latest 32 warnings. Individual components are captured coherently; independent workers are not paused for a global transaction.

Subscribe to `StateChanged` first, then read the initial snapshot:

```csharp
void Refresh()
{
    DownloadSessionSnapshot snapshot = session.GetSnapshot();
    foreach (ThreadSnapshot thread in snapshot.Threads)
    {
        Console.WriteLine($"{thread.Id}: {thread.Activity}, " +
            $"{thread.CompletedMedia}/{thread.KnownMedia}, " +
            $"queued={thread.Downloads.Queued}, active={thread.Downloads.Active}");
    }
}
session.StateChanged += Refresh;
Refresh();
```

`StateChanged` is an asynchronous invalidation signal, not a stream containing every intermediate transition. Each subscription has at most one running callback and one pending invalidation. Slow or throwing observers cannot block another subscriber or the session's download control path. UI consumers marshal their projection to their own dispatcher and fetch a fresh snapshot when notified.

Unsubscribe when a client detaches. Unsubscription or disposal discards pending invalidations and stops new callback admission; a callback already admitted for execution may still run or finish afterward. Session shutdown does not wait for arbitrary consumer callbacks. Hosts must keep their callback targets valid or guard their own lifetime accordingly.

## Provider and diagnostic contracts

Implement `IImageBoard` and the board/thread contracts, or derive from the provided monitoring bases, and pass the provider instance to the constructor. Provider names are stable, distinct source identities. A custom provider's `UpdateMonitoringInterval` must accept validated session intervals without I/O or exceptions, applying any documented provider minimum by clamping. URL classification and object creation must stay passive. Network calls, catalog iteration and media admission must propagate the lifetime cancellation token.

Provider discovery/removal sinks are exclusive session-control registrations, separate from consumer notifications. Their `IDisposable` registration belongs to the session; applications observe `StateChanged` and snapshots instead of installing another control owner.

Core components accept an optional caller-owned Serilog `ILogger` and are silent by default. The library neither reads nor configures the global logger. Pass a logger to the registry, provider and queue components whose diagnostics the host wants to receive; their lifetimes must not outlive a disposed logger.

The built-in default HTTP clients share the provider request policy. Supplying a custom `HttpClient` is an advanced injection path; use `ProviderHttpClientFactory.CreatePolicyClient` when the same request policy is required, and follow its ownership/cancellation contract. Never dispose or mutate process-owned default clients.

The [package consumer sample](../samples/LibOChan.Consumer/README.md) is the compiled reference for package restore and a custom provider. The library contains no Avalonia UI, daemon, dynamic plugin loader or HTML archive implementation.

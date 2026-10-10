# libochan

`libochan` is the .NET 10 media-download library used by the oChan desktop application. It includes the 4chan and 8kun adapters, a shared download queue, verified file storage and optional session persistence. It has no Avalonia dependency and reads no desktop configuration files. Existing namespaces remain `oChan`, `oChan.Interfaces`, `oChan.Downloader` and `oChan.Boards.*`.

The package is prepared locally by this repository. Local packaging does not imply that version 0.8.0 has been published or that every platform release check has passed.

## Start a session

The host supplies providers, output location and limits explicitly. This example watches the URL passed as the first command-line argument until Enter is pressed:

```csharp
using oChan;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Interfaces;

string outputDirectory = Path.GetFullPath("downloads");
IImageBoard[] providers = new IImageBoard[]
{
    new FourChanImageBoard(),
    new EightKunImageBoard()
};
DownloadQueue queue = new DownloadQueue(
    maxParallelDownloads: 3,
    maxBandwidthBytesPerSecond: 1024 * 1024);

await using Registry session = new Registry(
    providers, queue, outputDirectory,
    threadInterval: TimeSpan.FromSeconds(60),
    boardIntervalSeconds: 60,
    persistSession: false);

AdmissionResult result = await session.AddAsync(args[0]);
Console.WriteLine(result.Status);
Console.ReadLine();
await session.StopAsync();
```

`Added` means that the watch was admitted and started. It does not mean that its first source request or media downloads have completed. `AlreadyPresent`, `Invalid` and `Unsupported` are separate admission outcomes. Operational failures such as persistence errors are reported to the caller through exceptions.

## Ownership and persistence

Construction is passive. `Registry` owns watch lifetimes and the supplied queue's shutdown. Use one queue per session. `StopAsync` and `DisposeAsync` stop further work, cancel active requests and transfers, and await cleanup. Repeated shutdown is valid; a new run uses a new session and fresh provider resources. Provider transports and profile leases supplied by the host remain host-owned and must outlive the session.

Registry mutation commands can be called concurrently; admission, persistence and removal decisions are serialized internally. Snapshots can be read from any thread. Await the initial restore before submitting user commands, and use the registry's lifecycle methods rather than starting or stopping its provider resources independently. A stopped session rejects new admissions.

For durable watches, acquire `ProfileLease.Acquire(profileDirectory)`, create `SessionStore(profileDirectory, lease)`, pass that store to `Registry`, and await `RestoreAsync()` before accepting new mutations. Keep the lease alive until the registry has stopped. Successful persistent Add/Remove calls await the session commit; closing the application does not erase tracked intent. The built-in store allows one writer per profile. Separate profiles can coexist; media publication uses per-directory leases between cooperating writers.

Removing a board stops catalog discovery while admitted threads continue. Removing a thread stops its work and suppresses automatic rediscovery; explicitly adding its URL clears that suppression. `SessionOptions` with `ApplyOptionsAsync` updates queue limits and future polling delays. Existing watches retain their assigned output roots. New directly added watches use the new root; threads discovered by an existing board inherit that board's assigned root. Persistence and settings-write errors are observable by the host.

Files are stored under `{root}/{source}/{board}/{thread}/{mediaId}.{ext}`. The store verifies available size/hash evidence, publishes the complete file, then commits its manifest. Existing foreign files are preserved. A new store verifies saved media before reuse. Within a store, unchanged file fingerprints can reuse verification for up to ten minutes; preserving every fingerprint during an external edit can defer detection until that deadline.

## State and provider extensions

`Registry.GetSnapshot()` returns defensive `DownloadSessionSnapshot`, `ThreadSnapshot` and `BoardSnapshot` values. Canonical `ResourceId` values identify source, board and thread. Thread snapshots expose typed `Activity` and `SourceState`, request admission/cooldown progress, numeric media totals, actual queued/active worker counts, and `FailureInfo` values with categories and available HTTP status codes. A source can be archived or gone while its known pending media still complete. The legacy formatted `Status`/`Progress` properties remain display conveniences; consumers use snapshots for decisions. Registry membership, individual resources and queue counts are captured coherently at their own boundaries; snapshot creation does not freeze independent workers into one global transaction.

In-process clients that also remove resources can use `Registry.GetView()`. Its read-only dictionaries pair the exact `IThread`/`IBoard` handles with snapshots from the same membership capture. This keeps removal commands tied to the displayed lifetime even if the same canonical URL is removed and added again. `GetSnapshot()` is the simpler choice for consumers needing only copied state.

Subscribe to `Registry.StateChanged` before reading the first snapshot, then reread after each signal. Notifications are asynchronous invalidations; intermediate changes can coalesce. Each subscription has at most one executing callback and one pending invalidation. Slow or throwing subscribers cannot block producers or other subscribers. Unsubscribing and disposal discard pending notifications without waiting for arbitrary consumer code; an already executing callback may finish afterward. A permanently blocked synchronous handler retains its one dispatch worker until it returns. GUI clients marshal and coalesce their own UI updates. Base-resource `PropertyChanged` follows the same policy and an empty property name means reread all properties. Snapshot warnings retain the latest 32 entries; awaited command completion remains the authority for a successful durable change.

Logging is optional and explicit: pass a host-owned `Serilog.ILogger` through the `logger` parameters of the registry, queue and providers. They default to a no-op logger and neither replace nor read the global `Log.Logger`. Workers/limiters inherit the queue logger and resources inherit their provider's logger. The library never disposes a supplied logger. Logging sinks should return promptly; their execution is separate from state-notification isolation.

Register a custom `IImageBoard` in the constructor. `BaseImageBoard`, `BaseBoard` and `BaseThread` supply the standard lifecycle for adapters. Resolvers and constructors must be passive, return canonical identities, and create fresh resources for fresh lifetimes. Provider code propagates cancellation, assigns typed activity, and submits `DownloadItem` instances to the session queue. Source IDs must be unique within a session and path components must meet `StorageLayout`'s portable rules. Discovery/removal sink registrations belong exclusively to the session owner and provide reliable control; consumers observe `StateChanged` instead. The package-only console sample demonstrates this using only public APIs and an entirely synthetic HTTP handler.

Custom `UpdateMonitoringInterval` implementations must accept intervals validated by `SessionOptions.Validate` without throwing or performing I/O. They update the next waiting period; stricter provider minimums may increase the effective delay. This contract allows `ApplyOptionsAsync` to commit settings and then apply the already validated runtime values consistently. The supplied base implementations follow this contract.

Built-in default clients share rate admission and cooldown policy. An injected `HttpClient` is an advanced host-owned transport: wrap real HTTP with `ProviderHttpClientFactory.CreatePolicyClient` and share `RequestPolicyState` when limits must be coordinated. The underlying transport must disable automatic redirects/retries and honor cancellation. Caller-owned policy clients are disposed after session shutdown; process-owned default clients must not be disposed or mutated.

## Build and verify the package

From the repository root:

```sh
dotnet test oChan.sln -c Release
dotnet pack libochan/libochan.csproj -c Release -o artifacts/packages
python3 scripts/test-local-package.py --feed artifacts/packages --consumer samples/LibOChan.Consumer/LibOChan.Consumer.csproj
```

Use a fresh feed containing one candidate package. The verifier restores the sample with a fresh package cache and forces `libochan` to come from that local feed, checks the restored package hash and excludes desktop packages from the dependency graph. The sample performs a verified download, starts a fresh session over the same files, confirms reuse without another media request, and stops cleanly. It contacts no public imageboard.

`libochan` is licensed under Artistic License 2.0; the package includes `LICENSE`. Its NuGet dependencies retain their own licenses. This package contains no Actipro or Avalonia binaries, desktop assets or desktop redistribution terms.

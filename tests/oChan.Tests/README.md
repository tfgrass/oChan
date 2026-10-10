# oChan.Tests

The xUnit/VSTest project references `libochan/libochan.csproj` directly. Its dependency graph contains no Avalonia or desktop-project reference. Run it from the repository root:

```sh
dotnet test tests/oChan.Tests/oChan.Tests.csproj -c Release
```

The regression suite exercises Registry admission and watch lifetimes, both real adapters, polling, the shared download queue and worker, bandwidth and HTTP policies, media verification and recovery, and session persistence. Queue tests cover global worker limits, cancellation barriers, destination identity, and immutable per-thread snapshots. Desktop settings and the Add-input helper are covered by the separate [oChan.DesktopTests](../oChan.DesktopTests/README.md) project.

HTTP responses come only from synthetic `HttpMessageHandler` implementations. No test connects to a public imageboard or reads the real clipboard or user configuration. Adapter subclasses replace the protected delay hook with explicit steps; the 100-cycle catalog checks therefore take no real polling intervals. Controlled gates hold headers or response bodies to test concurrency, cancellation, and awaitable removal. All files live in temporary test directories with explicit profiles. Selected persistence and media-store tests start child test processes to exercise abrupt termination and cross-process leases.

The suite does not start Avalonia. Desktop smoke coverage and its execution requirements are documented separately in [oChan.DesktopSmoke](../oChan.DesktopSmoke/README.md). The tests cover synthetic provider contracts; they do not establish compatibility with future changes to public provider APIs.

Run `dotnet test oChan.sln -c Release` to execute both the core and desktop unit-test projects. The package-only [console consumer](../../samples/LibOChan.Consumer/README.md) verifies that a freshly packed library can also be used without any source-project reference.

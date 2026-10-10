# Package-only libochan consumer

This .NET 10 console application references the local `libochan` NuGet package. It has no project reference to either the library source or the desktop application.

The example registers its own provider through the public API, serves deterministic bytes from an injected `HttpMessageHandler`, and lets the real registry, queue and media store perform the download. It serializes the typed `ThreadSnapshot` from `Registry.GetSnapshot()` as JSON and checks the saved bytes, media totals and actual queue counts. A second session with fresh provider, queue and store instances reuses the committed file; the handler must receive exactly one media request across both sessions. Both sessions shut down before their temporary output is deleted.

The first-poll completion in `DemoThread` is specific to this fixture. Waiting for queue idle immediately after `AddAsync` would race the asynchronous first poll, so the sample waits for that poll to enqueue its medium first.

Run the repository's isolated package verifier:

```sh
dotnet pack libochan/libochan.csproj -c Release -o artifacts/packages
python3 scripts/test-local-package.py --feed artifacts/packages --consumer samples/LibOChan.Consumer/LibOChan.Consumer.csproj
```

Use a fresh feed containing one candidate. The verifier selects its version, applies `-p:LibOChanVersion`, restores from a fresh NuGet cache with `libochan` mapped exclusively to the local source, and checks that the resolved graph contains no desktop dependencies. Without an override, the sample uses the repository's product version from `Directory.Build.props`. The verifier also checks the restored package's SHA-256 against the candidate before running the sample. External dependency packages may be downloaded from nuget.org during restore; the running example performs no external network requests.

This sample remains outside `oChan.sln` so ordinary source builds do not depend on a previously packed library.

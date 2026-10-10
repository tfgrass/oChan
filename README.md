# oChan

oChan watches threads and boards on 4chan and 8kun and downloads their media. The Avalonia desktop app uses the independently consumable `libochan` library. Other applications can supply providers through the library contracts.

Version **0.8.0** is being prepared as a stabilization release on the way to 1.0. The [release notes](CHANGELOG.md) describe the changes and upgrade considerations; the [platform validation record](docs/platform-support.md) distinguishes completed checks from outstanding native platform tests.

## What's new in 0.8.0

- More reliable 4chan and 8kun downloads, with a shared queue, request pacing, bounded retries and cancellation.
- Verified files and atomic writes prevent partial downloads from being counted as complete. Saved media is checked again when needed.
- Watched URLs are saved as they change when session restoration is enabled; restarting no longer depends on a graceful previous exit.
- Clearer progress, queued/active counts, numeric sorting, corrected context menus and visible settings errors.
- An independent `libochan` NuGet package with explicit provider registration and a console consumer example.
- .NET 10 builds for Windows x64, Linux x64/Arm64 and macOS Intel/Apple Silicon, with self-contained archives and macOS app bundles.

## Build and run

Use the exact .NET 10 SDK selected by `global.json` from a source checkout. Release builds use reviewed dependency lockfiles; [updating them](docs/platform-support.md#dependency-restores-and-updates) is an explicit maintainer step:

```sh
dotnet restore oChan.sln -p:Configuration=Release --locked-mode
dotnet build oChan.sln -c Release --no-restore
dotnet run --project oChan.csproj -c Release --no-build --no-restore
```

Desktop builds target Windows, macOS and Linux. Self-contained archives include the .NET runtime, so users do not need to install the SDK. Extract the complete archive: native libraries and license files beside the executable are required. macOS archives contain an unsigned `oChan.app` bundle that can be opened in Finder. Native macOS launch and Gatekeeper behavior still require release validation. See [platform requirements and package checks](docs/platform-support.md).

## Downloading

Enter a thread or board URL and select **Add**. A board watch discovers its threads automatically. Selecting **Add** with an empty field imports the clipboard once. With text in the field, only that text is added.

The thread list shows source availability and media progress separately. Rate limits and temporary HTTP failures are retried within bounded limits; cancellation waits for active work to stop. Use the row's folder action to open that thread's assigned destination.

Media is stored under:

```text
<download root>/<source>/<board>/<thread>/<media ID>.<extension>
```

Completed media is verified and recorded in a manifest. Files are published atomically; unrelated files are preserved. Interrupted transfers restart from the beginning. Removing a board stops discovery while its existing threads continue. Removing a thread also suppresses its automatic rediscovery; explicitly adding its URL allows it again.

## Settings and saved sessions

Settings control the output path, polling interval, global parallel downloads and bandwidth. Existing watches retain their assigned destinations when the output root changes. Threads added directly use the current root; threads discovered from an existing board keep that board's assigned root.

When **Restore session on startup** is enabled, confirmed changes to watched URLs are saved as they happen. The application restores them at startup. Settings/session failures are shown rather than silently replacing saved state. Only one running application can own a profile.

The default profile is `%APPDATA%/.oChan` on Windows and `$HOME/.oChan` on macOS/Linux. Set `OCHAN_PROFILE` to an absolute directory to use an isolated profile. The default download root is `Downloads/oChan` under the user home directory.

## Library

The library project is `libochan/libochan.csproj`; package consumers keep the existing `oChan` namespaces. The desktop configuration, windows and Avalonia dependencies belong to the reference client.

```sh
dotnet pack libochan/libochan.csproj -c Release -o artifacts/packages
```

The [package consumer sample](samples/LibOChan.Consumer/README.md) shows how to restore the local package into a fresh cache and run a custom provider without the desktop app. The [session API](docs/Registry.md) describes lifetime and persistence ownership; the [download pipeline](docs/Downloader.md) explains completion and cancellation.

Provider registration is explicit. Dynamic plugin loading, a daemon, TUI, terminal browser and HTML archive are future work.

## Verification

```sh
dotnet test oChan.sln -c Release
dotnet build tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release
python scripts/run-desktop-smoke.py --assembly tests/oChan.DesktopSmoke/bin/Release/net10.0/oChan.DesktopSmoke.dll --output artifacts/desktop-smoke
```

The graphical harness requires a working desktop on Windows/macOS and `xvfb-run` on Linux. It checks the real Avalonia application with isolated local fixtures in normal, locked-profile, absent-tray and failing-tray modes. Offline tests and the harness make no public imageboard requests. Running the harness does not replace starting a freshly extracted release archive.

## License

The project uses Artistic License 2.0; see [LICENSE](LICENSE). Desktop dependency and asset notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

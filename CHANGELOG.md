# Changelog

## 0.8.0 (unreleased preview)

This update rebuilds download and session handling for 4chan and 8kun and makes `libochan` usable independently of the desktop app.

### What's new

- **More reliable source checks.** Updated 8kun media URLs, canonical URL handling and conditional thread requests reduce broken or duplicate work. Archived or missing sources remain distinguishable from unfinished media downloads.
- **Controlled HTTP requests.** Shared request pacing, bounded retries, server cooldowns and request/body deadlines handle rate limits and temporary failures. Stopping a watch cancels its requests and waits for cleanup.
- **Verified downloads.** Size and checksum checks, atomic file publication and manifests prevent partial files from appearing complete. Existing unrelated files are preserved. Rechecks retry failed media and repair missing or changed files even when the thread response is unchanged; cached verification reduces repeated disk reads.
- **One shared download queue.** Parallel-download and bandwidth limits apply across all watched threads. Duplicate jobs are suppressed, and removal waits for the affected downloads to stop. Removing a board leaves its admitted threads running; removing a thread prevents automatic rediscovery until it is explicitly added again.
- **Durable sessions and settings.** With session restore enabled, watch changes are saved as they happen. Saved sessions retain removed-thread suppression and assigned download roots. Settings are validated before use, save failures are visible, and recovery preserves damaged files. Changing the output root keeps existing watches in their assigned directories.
- **Desktop fixes.** Thread rows show source state, media progress and queued/active counts. Sorting handles numeric values correctly, context actions target the clicked row, and folder actions open the assigned destination. Clipboard import, settings dialogs and tray fallback are more reliable; Exit remains accessible without a working tray.
- **Standalone library.** `libochan` is a separate library project with local NuGet packaging, without Avalonia or desktop configuration. It provides explicit provider registration, asynchronous session control, typed snapshots and isolated state notifications. Logging is optional and silent by default. A package consumer sample verifies download and reuse through public APIs.

### Upgrade notes

- **.NET 10 replaces .NET 8.** Source builds use the SDK pinned in `global.json`; library consumers require .NET 10. Self-contained desktop packages include the runtime.
- **Download paths now separate providers:** `<root>/<source>/<board>/<thread>/<media ID>.<extension>`. The default root is now `Downloads/oChan` under the user home directory; existing settings retain their configured root. Legacy files discovered through current provider metadata are imported only when size and MD5 match, leaving the originals untouched. Old completion markers alone are not trusted; interrupted transfers restart from the beginning.
- **Profiles keep their existing locations:** `%APPDATA%/.oChan` on Windows and `$HOME/.oChan` on macOS/Linux. Older saved URL lists are accepted when session restore is enabled. Only one application instance may own a profile; use an absolute `OCHAN_PROFILE` directory for a separate instance.
- **Library integration changes are breaking.** Source integrations must adopt explicit dependencies, asynchronous session operations and snapshot notifications. Desktop configuration is no longer part of the library. See the [session API](docs/Registry.md) and [package consumer sample](samples/LibOChan.Consumer/README.md).

### Packages and validation

Packaging targets Windows x64, Linux x64/Arm64 and macOS Intel/Apple Silicon. Release builds use a pinned SDK and reviewed dependency locks, with expanded offline tests, graphical checks and package validation. Extract the complete archive, including its native libraries and notices.

Final native acceptance of the extracted packages is still outstanding across the target matrix. macOS bundles are unsigned and not notarized; native launch and Gatekeeper validation remain open. Linux requires glibc and an X11 or XWayland desktop. The [platform validation record](docs/platform-support.md) lists prerequisites and the evidence available for each target.

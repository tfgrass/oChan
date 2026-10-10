# Roadmap

The current work stabilizes oChan's existing thread and board downloads for a 0.8.0 release. The desktop app remains cross-platform, and `libochan` is independently consumable by other .NET applications. A prepared package is not a published or fully accepted release.

## Implemented stabilization scope

- One session owns watches, cancellation and a shared, bounded download queue. Duplicate admissions and late callbacks cannot create overlapping watch lifetimes.
- Verified media files are published atomically with manifests. Existing files, interrupted writes and restore failures have explicit recovery behavior.
- Built-in 4chan and 8kun providers share request pacing, backoff and bounded deadlines. Source availability and media completion are separate states.
- Confirmed session changes persist without depending on a graceful application exit. Settings apply through the active session, with assigned destinations preserved.
- `libochan` has its own project/package and a package-only custom-provider sample. Typed snapshots and bounded observer notifications support clients without Avalonia.
- Desktop input, settings, startup errors, shutdown and tray fallback use the same session behavior.

The [session API](Registry.md), [download pipeline](Downloader.md), and [library sample](../samples/LibOChan.Consumer/README.md) describe the actual interfaces.

## Remaining release acceptance

CI prepares offline tests, native graphical harness runs, isolated package-consumer validation and self-contained archives. macOS candidates use an unsigned `.app` bundle inside tar.gz. Final acceptance requires evidence from the actual extracted apphost on each promised OS/CPU, including download, cancellation, restart/restore and native desktop interactions.

The [platform and release record](platform-support.md) lists the candidate matrix, upstream requirements and the distinction between source-built harness tests and packaged executable tests. Native Windows/macOS evidence is unavailable on the current Linux development host. Cross-publish success does not close those gates.

## Later work

TUI, daemon/client connections, a terminal board browser, dynamic plugin loading, HTML/offline archives, thumbnails and additional convenience features remain separate work. Providers can already be supplied explicitly through the library contracts. These future features do not expand the current stabilization release.

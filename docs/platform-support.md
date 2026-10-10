# Platform requirements and release evidence

This record separates upstream platform support, candidate oChan packages, and checks actually performed. A successful cross-publish does not establish that an application starts on the target operating system or CPU.

## Upstream requirements checked on 2026-10-10

The official [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), updated 2026-09-28 when checked, lists:

- macOS 15, 26 and 27 on x64/Arm64. macOS 14 is listed out of support.
- Ubuntu 22.04, 24.04 and 26.04 on x64/Arm64, among other listed distributions. The portable x64/Arm64 glibc baseline is 2.27; this is a runtime compatibility floor, not a guarantee for every distribution or native graphics dependency.
- Maintained Windows 11 releases/editions and the specifically listed Windows 10 LTSC/Enterprise/IoT variants. It does not promise general support for every Windows 10 installation. The [Microsoft installation guidance](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions) explains the edition/lifecycle restriction.

The current Avalonia documentation also covers Avalonia 12, including a 12.1 Wayland package, and must not be used as though all of it describes the project's Avalonia 11 packages. The [official documentation snapshot from the Avalonia 11 era](https://github.com/AvaloniaUI/avalonia-docs/blob/4640a690ff98762519cd16ab369a0e8016aa3c59/docs/overview/supported-platforms.md) lists Windows 8.1/10/11, macOS 10.14–15, Debian 9+, Ubuntu 16.04+ and Fedora 30+. Those older operating-system allowances do not override .NET 10's stricter supported-platform matrix. The snapshot is historical upstream evidence, not a versioned warranty for every Avalonia 11 patch.

The current Linux desktop uses X11, including XWayland on a Wayland desktop. A working graphical session and native graphics/font libraries are required even in a self-contained package. Ubuntu requires the X11/font dependencies used by Avalonia, including `libx11-6`, `libice6`, `libsm6` and `libfontconfig1`. A `linux-x64` or `linux-arm64` package targets glibc; it is not a `linux-musl-*` Alpine package.

## Candidate package matrix

| Runtime identifier | Archive | Intended native validation baseline | Evidence still required |
| --- | --- | --- | --- |
| `win-x64` | `.zip` | Maintained Windows 11 x64 | Final package startup and desktop workflows on Windows 11. A Server 2022 CI test is useful but is not that OS test. |
| `linux-x64` | `.tar.gz` | Ubuntu 24.04 x64 | Final extracted package; the development host's successful Manjaro desktop harness is separate evidence. |
| `linux-arm64` | `.tar.gz` | Ubuntu 24.04 Arm64 | Native Arm64 package and desktop run; x64 cross-publish is insufficient. |
| `osx-x64` | `.tar.gz` | macOS 15 x64 | Native Intel Mac package and desktop run. |
| `osx-arm64` | `.tar.gz` | macOS 15 Arm64 | Native Apple Silicon package and desktop run. |

These are candidate validation baselines, not completed platform certifications. Exact supported OS releases will be recorded with the tested artifacts. Microsoft/Avalonia support policies can change with OS end-of-life; consult the linked current policy before expanding the product matrix.

## Current distribution form

Windows candidates contain a self-contained executable directory in a zip archive; Linux candidates contain the equivalent directory in tar.gz. Extract every file and launch `oChan.exe` on Windows or `./oChan` on Linux. Keep the accompanying native libraries and notices beside the executable. macOS tar.gz archives contain `oChan.app`; open that bundle in Finder or with `open oChan.app`.

The macOS bundle is unsigned and is not notarized. Its structure contains the complete publish output under `Contents/MacOS`, the existing application icon under `Contents/Resources`, and `Info.plist` metadata generated from the product version. The bundle identifier is `org.ochan.desktop`, executable name `oChan`, and candidate deployment baseline macOS 15.0. Local structural checks do not establish native launch or Gatekeeper acceptance. The [official Avalonia macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos) describes that structure. Its example `LSMinimumSystemVersion` is an example, not this application's supported minimum.

Packaging a `.app` can be prepared without credentials. Signing/notarization and public distribution require separate decisions and native Mac validation; no signing identity or publishing account is required for the local checks in this repository.

## What each check proves

| Check | Evidence provided | Remaining boundary |
| --- | --- | --- |
| Offline tests | Deterministic production behavior against local fixtures, including failures and cancellation. | No public provider availability or native UI proof. |
| Native source-built desktop harness | Avalonia startup/bindings/shutdown and selected profile/tray error paths on that runner. | The harness is a separate executable, even when it references the same application assembly. It does not execute the published `oChan` apphost. |
| Publish and archive round-trip | Target output can be created; full files, notices and Unix executable bits survive archiving/extraction. | No proof that the extracted app starts or downloads correctly. |
| Native extracted app run | Actual apphost, native dependencies, resources and normal app entrypoint run on the recorded OS/CPU. | Other OS/CPU combinations remain untested. |
| Local NuGet package consumer | A fresh restore can consume the candidate library with no project reference or desktop dependency. | No public NuGet publication is performed. |

CI attempts graphical harness runs on Windows/macOS runners and uses Xvfb on Linux. A runner without a working display must report the check as failed or unavailable; it must not silently count it as passed. A tray-absence simulation tests the application fallback contract and does not establish every desktop's tray behavior.

## Final artifact acceptance record

For every platform promised as supported, record the source commit, SDK/runtime and resolved dependency graph, archive SHA256, actual OS/version/CPU, and native test result. Extract to a fresh directory and start from a different working directory using an isolated `OCHAN_PROFILE`. Verify:

1. The actual packaged application starts, loads icons/resources and reports the intended version.
2. Add, bounded download, cancellation, restart/restore and assigned download directories work.
3. Removing boards/threads has the documented behavior; settings persistence and an already-owned profile are handled visibly.
4. Clipboard, two-row context menus, folder picker and folder opening operate on the intended data.
5. Normal Exit works; unavailable/failing tray support leaves an accessible application.
6. `LICENSE`, `README.md` and `THIRD-PARTY-NOTICES.md` accompany the complete package; Unix executable permissions survive extraction.

The current local environment cannot provide native Windows or macOS evidence. Preparing workflow jobs does not mean they have run. Cross-published ARM64 or macOS artifacts remain candidates until the corresponding native evidence exists.

## Dependency restores and updates

`global.json` requires SDK 10.0.111 without patch roll-forward. For the five repository production/test projects, Release restores use reviewed `packages.release.lock.json` files and fail on dependency drift. Each published runtime has its own `packages.release.<RID>.lock.json` in the desktop and library projects. Switching a runtime therefore does not overwrite the portable or another runtime's lock. Default Debug development restores remain independent of this Release policy.

Use the same configuration and publish properties during restore and build:

```sh
dotnet restore oChan.sln -p:Configuration=Release --locked-mode
dotnet build oChan.sln -c Release --no-restore
dotnet test oChan.sln -c Release --no-build --no-restore
dotnet restore tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -p:Configuration=Release --locked-mode
```

For a self-contained package, replace `<RID>` with one of the five candidate identifiers:

```sh
dotnet restore oChan.csproj -p:RuntimeIdentifier=<RID> -p:Configuration=Release -p:SelfContained=true -p:PublishSingleFile=true --locked-mode
dotnet publish oChan.csproj -c Release --runtime <RID> --self-contained true -p:PublishSingleFile=true --no-restore --output artifacts/publish/<RID>
```

The restore deliberately sets the singular `RuntimeIdentifier` property. `dotnet restore --runtime` sets the plural `RuntimeIdentifiers`; with a self-contained executable, the SDK can then infer the current host as an additional runtime. Selecting the target explicitly keeps each reviewed runtime lock independent of the build host.

SDK and dependency upgrades deliberately regenerate locks. After changing the version, run the corresponding restore command with `-p:RestoreLockedMode=false --force-evaluate` instead of `--locked-mode`, first for the solution and smoke project, then for every supported publish RID with the same `SelfContained` and `PublishSingleFile` properties. Review the lock diffs, repeat all seven restores in locked mode, and run the relevant tests before committing the upgrade. CI also treats an unavailable vulnerability-data query (`NU1900`) as an error.

The package-only sample is outside this policy: it restores the just-built candidate into a fresh package cache and verifies its exact package hash. A fixed sample lock would otherwise reject intentional new contents of a local candidate at the same version. The SDK and package locks fix the selected inputs; they do not claim byte-for-byte identical archives. CI retains the resolved graph, SDK details and artifact hashes as evidence.

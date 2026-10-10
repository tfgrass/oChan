# Desktop smoke harness

Run against a working graphical desktop, for example on Linux:

```sh
dotnet build tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build -- --locked-profile
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build -- --no-tray
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build -- --tray-failure
```

For the Linux CI environment, install `xvfb` and `dbus-daemon`, then run all four modes with retained logs, screenshots and exit codes:

```sh
python scripts/run-desktop-smoke.py --assembly tests/oChan.DesktopSmoke/bin/Release/net10.0/oChan.DesktopSmoke.dll --output artifacts/desktop-smoke
```

The runner creates a private `dbus-run-session` and an Xvfb server for each Linux process. It sets the harness-only `OCHAN_DESKTOP_SMOKE_XVFB=1` environment option, which selects X11 software rendering before Avalonia initializes and installs the tray fixture because Xvfb provides no StatusNotifierWatcher. Normal mode still exercises all normal GUI checks; `--tray-failure` still simulates a tray creation exception. This environment is recorded in the logs and `results.json`. It does not validate GPU rendering or a native tray host. Direct desktop runs retain normal renderer and tray setup; Windows/macOS are unchanged. Avalonia errors and binding warnings continue to fail every run.

The harness creates and removes its own temporary `OCHAN_PROFILE`, including an isolated download path. It does not load the user's session. No production test hooks are required.

It starts the real application, waits for session restoration, renders local thread and board rows, and verifies the final title after a burst of 1,000 worker-thread updates. It also checks request admission/cooldown display, typed queued/active counts, retained check errors, terminal source state, board request progress, and disposal of row subscriptions. Normal `MainWindow.Close()` must finish, after which the harness reacquires the profile lease to verify shutdown released it. Fixture providers reject HTTP requests. Avalonia binding warnings and errors fail the smoke run.

The normal run also checks numeric sorting with values 2 and 10, context targeting for two thread/board rows (including Ctrl-right-click through a hit-tested cell), clearing stale selection on blank space, and visibility of the Active column at the default window width. Settings checks use temporary profiles and a scoped fake storage provider to cover pending/duplicate saves, close behavior, validation, save failures and retries, and folder-picker cancellation/errors/results. These checks never open a native file picker.

On Linux/X11, a fixture clipboard service is installed before the window is created. The harness verifies its identity before using Add, then covers explicit input, multiline clipboard import, overlapping clicks, empty content and read failures. It never reads or writes the native desktop clipboard. Other platforms explicitly skip these clipboard checks; their native clipboard behavior remains untested.

The `--locked-profile` case holds a conflicting lease before application startup and checks that a readable startup-error window appears and its Exit button closes normally. Window-only PNG renders are saved beside the smoke executable.

The `--no-tray` and `--tray-failure` cases keep native windows and rendering, but replace tray creation with a null result or an exception. With `MinimizeToTray` enabled, they invoke the native user-close callback and verify that the window remains minimized with taskbar access. They restore the window and use its visible Exit menu item to finish shutdown. Unknown or combined arguments fail with exit code 2. Only this non-packable harness opts into Avalonia's unstable windowing-platform service API; production code uses public tray APIs.

Avalonia 11.3 does not expose reliable tray-host availability. The application keeps taskbar access until a real tray activation has demonstrated an available route back to a hidden window. A later loss of that host is not observable through the public API. Tray setup failures therefore do not prevent startup. During Linux shutdown, the application detaches the DBus menu and flushes pending dispatcher layout updates before disposing the icon; this avoids a reproduced DBus menu callback accessing its disposed exporter. Windows and macOS dispose the native tray directly. Assigning `Menu=null` on Avalonia 11.3.22's macOS tray exporter instead replaces the managed menu while retaining a native menu tied to the old instance, which throws `ArgumentException: The menu being updated does not match` and interrupts shutdown. CI shutdown logs confirmed that failure; the DBus workaround therefore applies only to Linux.

Run each mode as a separate process and check its exit code, not only the `SMOKE PASS` line. The independent Linux shutdown failure in [upstream issue 19523](https://github.com/AvaloniaUI/Avalonia/issues/19523) involved a DBus observer synchronously calling a stopped dispatcher. The desktop pins `Tmds.DBus.Protocol` to `0.92.0`, whose exception callback uses asynchronous `Post` while retaining the API required by Avalonia 11.3.22. The isolated dependency probe passed five normal shutdown runs and all three additional modes on 2026-10-10; this is separate from the tray menu lifecycle correction above.

The harness enables application Debug logs to retain shutdown phases and caught exceptions, and records closing events, the return from normal `Close()`, and remaining owned-window state after the settings checks. On a macOS process timeout, the runner attempts a three-second native `sample` with an eight-second diagnostic limit before terminating the process. The sample is retained beside the mode log; the run still fails with exit code 124. These diagnostics do not replace the required `Closed` event, successful process exit, or profile-lease reacquisition.

This checks native desktop startup and lifetime integration on the OS and CPU recorded for each run, using synthetic input events and isolated platform-service fixtures. Windows and macOS harness results are native UI evidence for their respective runners; the [platform validation record](../../docs/platform-support.md#recorded-validation) identifies completed runs. The harness does not execute the extracted release apphost or test real HTTP services, downloads, native clipboard/file-picker integration or every tray host. Full workflow acceptance of the final extracted packages remains a separate release check.

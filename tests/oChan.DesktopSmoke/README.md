# Desktop smoke harness

Run against a working graphical desktop, for example on Linux:

```sh
dotnet build tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build -- --locked-profile
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build -- --no-tray
DISPLAY=:1 timeout 30s dotnet run --project tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release --no-build -- --tray-failure
```

The harness creates and removes its own temporary `OCHAN_PROFILE`, including an isolated download path. It does not load the user's session. No production test hooks are required.

It starts the real application, waits for session restoration, renders local thread and board rows, and verifies the final title after a burst of 1,000 worker-thread updates. It also checks request admission/cooldown display, typed queued/active counts, retained check errors, terminal source state, board request progress, and disposal of row subscriptions. Normal `MainWindow.Close()` must finish, after which the harness reacquires the profile lease to verify shutdown released it. Fixture providers reject HTTP requests. Avalonia binding warnings and errors fail the smoke run.

The normal run also checks numeric sorting with values 2 and 10, context targeting for two thread/board rows (including Ctrl-right-click through a hit-tested cell), clearing stale selection on blank space, and visibility of the Active column at the default window width. Settings checks use temporary profiles and a scoped fake storage provider to cover pending/duplicate saves, close behavior, validation, save failures and retries, and folder-picker cancellation/errors/results. These checks never open a native file picker.

On Linux/X11, a fixture clipboard service is installed before the window is created. The harness verifies its identity before using Add, then covers explicit input, multiline clipboard import, overlapping clicks, empty content and read failures. It never reads or writes the native desktop clipboard. Other platforms explicitly skip these clipboard checks; their native clipboard behavior remains untested.

The `--locked-profile` case holds a conflicting lease before application startup and checks that a readable startup-error window appears and its Exit button closes normally. Window-only PNG renders are saved beside the smoke executable.

The `--no-tray` and `--tray-failure` cases keep native windows and rendering, but replace tray creation with a null result or an exception. With `MinimizeToTray` enabled, they invoke the native user-close callback and verify that the window remains minimized with taskbar access. They restore the window and use its visible Exit menu item to finish shutdown. Unknown or combined arguments fail with exit code 2. Only this non-packable harness opts into Avalonia's unstable windowing-platform service API; production code uses public tray APIs.

Avalonia 11.3 does not expose reliable tray-host availability. The application keeps taskbar access until a real tray activation has demonstrated an available route back to a hidden window. A later loss of that host is not observable through the public API. Tray setup failures therefore do not prevent startup. During shutdown, the application detaches the native menu and flushes pending dispatcher layout updates before disposing the icon; this avoids a reproduced DBus menu callback accessing its disposed exporter.

Run each mode as a separate process and check its exit code, not only the `SMOKE PASS` line. The independent Linux shutdown failure in [upstream issue 19523](https://github.com/AvaloniaUI/Avalonia/issues/19523) involved a DBus observer synchronously calling a stopped dispatcher. The desktop pins `Tmds.DBus.Protocol` to `0.92.0`, whose exception callback uses asynchronous `Post` while retaining the API required by Avalonia 11.3.22. The isolated dependency probe passed five normal shutdown runs and all three additional modes on 2026-10-10; this is separate from the tray menu lifecycle correction above.

This checks desktop startup and lifetime integration with synthetic input events and isolated platform-service fixtures. It does not test real HTTP services, downloads, OS clipboard or file-picker integration, a functioning native tray host, or native Windows/macOS behavior. Native platform smoke tests remain separate release checks.

# oChan.DesktopTests

These xUnit tests reference the actual desktop assembly to exercise its configuration recovery/save behavior and Add-input routing. They do not open a window, access the real clipboard or read the user's profile; settings files live in isolated temporary directories.

```sh
dotnet test tests/oChan.DesktopTests/oChan.DesktopTests.csproj -c Release
```

The separate [core suite](../oChan.Tests/README.md) references only `libochan`. Run `dotnet test oChan.sln -c Release` for both suites. Native window/tray checks remain in [oChan.DesktopSmoke](../oChan.DesktopSmoke/README.md).

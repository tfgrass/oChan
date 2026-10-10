using System.Collections.Concurrent;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using oChan;
using oChan.Boards.FourChan;
using oChan.Desktop;
using oChan.Downloader;
using oChan.Http;
using oChan.Interfaces;
using oChan.Persistence;

namespace oChan.DesktopSmoke;

internal static class Program
{
    private static bool _checksPassed;
    private static bool _closed;
    private static Exception? _failure;
    private static bool _lockedProfileMode;
    private static bool _noTrayMode;
    private static bool _trayFailureMode;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--locked-profile" or "--no-tray" or "--tray-failure")))
        {
            Console.Error.WriteLine("Usage: oChan.DesktopSmoke [--locked-profile|--no-tray|--tray-failure]");
            return 2;
        }

        string profile = Path.Combine(Path.GetTempPath(), "ochan-desktop-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("OCHAN_PROFILE", profile);
        _lockedProfileMode = args.Contains("--locked-profile", StringComparer.Ordinal);
        _noTrayMode = args.Contains("--no-tray", StringComparer.Ordinal);
        _trayFailureMode = args.Contains("--tray-failure", StringComparer.Ordinal);
        bool xvfbEnvironment = OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("OCHAN_DESKTOP_SMOKE_XVFB") == "1";
        SmokeLogSink sink = new SmokeLogSink();
        try
        {
            Config config = new Config
            {
                DownloadPath = Path.Combine(profile, "Downloads"),
                MinimizeToTray = _noTrayMode || _trayFailureMode,
                SaveUrlsOnExit = true
            };
            config.SaveConfig();
            using ProfileLease? conflictingLease = _lockedProfileMode ? ProfileLease.Acquire(profile) : null;
            Logger.Sink = sink;
            Console.WriteLine("SMOKE isolated profile: " + profile);
            AppBuilder appBuilder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont();
            if (xvfbEnvironment)
            {
                Console.WriteLine("SMOKE Xvfb environment: X11 software rendering; no native tray host (tray fixture installed).");
                appBuilder.With(new X11PlatformOptions
                {
                    RenderingMode = new X11RenderingMode[] { X11RenderingMode.Software }
                });
            }
            int exitCode = appBuilder
                .AfterPlatformServicesSetup(builder =>
                {
                    if (!_lockedProfileMode)
                        ClipboardChecks.Install();
                    if (xvfbEnvironment || _noTrayMode || _trayFailureMode)
                    {
                        IWindowingPlatform platform = AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>();
                        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>()
                            .ToConstant(new NoTrayPlatform(platform, _trayFailureMode));
                    }
                })
                .AfterSetup(builder => Dispatcher.UIThread.Post(async () => await RunAsync(), DispatcherPriority.Background))
                .StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnMainWindowClose);

            if (exitCode != 0 || !_checksPassed || !_closed || _failure != null || !sink.Errors.IsEmpty)
            {
                Console.Error.WriteLine($"SMOKE FAILED: exit={exitCode}, checks={_checksPassed}, closed={_closed}, errors={sink.Errors.Count}; {_failure}");
                return 1;
            }

            if (!_lockedProfileMode)
            {
                using ProfileLease reacquired = ProfileLease.Acquire(profile);
                Console.WriteLine("SMOKE profile lease released after normal close.");
            }

            string scenario = _lockedProfileMode ? "locked profile: readable error, Exit closed the window"
                : _noTrayMode ? "no tray: user close kept taskbar access, Exit closed the window"
                : _trayFailureMode ? "tray creation failure: startup succeeded, user close kept taskbar access, Exit closed the window"
                : "startup, rendered thread/board bindings, background update, normal close";
            Console.WriteLine("SMOKE PASS: " + scenario + "; no binding errors.");
            return 0;
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task RunAsync()
    {
        Window? window = null;
        try
        {
            IClassicDesktopStyleApplicationLifetime desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime was not initialized.");
            window = desktop.MainWindow ?? throw new InvalidOperationException("No startup window was created.");
            window.Closed += (sender, e) =>
            {
                _closed = true;
                Console.WriteLine("SMOKE MainWindow.Closed observed.");
            };
            await WaitUntilAsync(() => window.IsVisible && window.Bounds.Width > 0);
            if (_lockedProfileMode)
            {
                if (window is not StartupErrorWindow)
                    throw new InvalidOperationException("A conflicting profile did not produce StartupErrorWindow.");
                await WaitUntilAsync(() => window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.IsVisible && block.Text?.Contains("Another oChan instance", StringComparison.Ordinal) == true));
                CaptureWindow(window, "smoke-startup-error.png");
                Button exit = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Exit");
                _checksPassed = true;
                exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }

            MainWindow mainWindow = window as MainWindow ?? throw new InvalidOperationException("MainWindow was not created.");
            await WaitUntilAsync(() => mainWindow.FindControl<Button>("AddUrlButton")!.IsEnabled);
            if (mainWindow.UrlList.Count != 0 || mainWindow.BoardsList.Count != 0)
                throw new InvalidOperationException("The isolated session did not start empty.");
            Console.WriteLine("SMOKE MainWindow visible with empty isolated session.");
            if (_noTrayMode || _trayFailureMode)
            {
                Func<WindowCloseReason, bool> requestClose = mainWindow.PlatformImpl?.Closing
                    ?? throw new InvalidOperationException("The native close callback is missing.");
                bool cancelled = requestClose(WindowCloseReason.WindowClosing);
                await WaitUntilAsync(() => mainWindow.WindowState == WindowState.Minimized);
                if (!cancelled || !mainWindow.IsVisible || !mainWindow.ShowInTaskbar || _closed)
                    throw new InvalidOperationException("Closing without a tray stranded or closed the window.");
                Console.WriteLine("SMOKE native user close kept the window minimized and accessible in the taskbar.");
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                await WaitUntilAsync(() => mainWindow.IsVisible && mainWindow.WindowState == WindowState.Normal);
                MenuItem exit = mainWindow.FindControl<MenuItem>("ExitMenuItem")
                    ?? throw new InvalidOperationException("The window Exit menu item is missing.");
                if (!exit.IsVisible || !exit.IsEnabled)
                    throw new InvalidOperationException("The window Exit menu item is not reachable.");
                CaptureWindow(mainWindow, _noTrayMode ? "smoke-no-tray.png" : "smoke-tray-failure.png");
                _checksPassed = true;
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return;
            }

            await ClipboardChecks.RunAsync(mainWindow);

            // Provider construction is passive; any attempted fixture request fails locally.
            using HttpClient client = new HttpClient(new RejectRequests());
            FourChanImageBoard provider = new FourChanImageBoard(client);
            SmokeBoard board = new SmokeBoard(provider, new Uri("https://boards.4chan.org/g/"));
            SmokeThread thread = new SmokeThread(board,
                new Uri("https://boards.4chan.org/g/thread/123"));
            thread.Title = "Desktop smoke initial title";
            using ThreadRow threadRow = new ThreadRow(thread);
            using BoardRow boardRow = new BoardRow(thread.Board);
            mainWindow.UrlList.Add(threadRow);
            mainWindow.BoardsList.Add(boardRow);
            await WaitUntilAsync(() => VisibleText(window, thread.Title));
            Console.WriteLine("SMOKE thread title binding rendered.");

            await Task.Run(() =>
            {
                for (int index = 0; index < 1000; index++)
                    thread.Title = "Desktop smoke burst " + index;
                thread.Title = "Desktop smoke updated title";
            });
            await WaitUntilAsync(() => VisibleText(window, "Desktop smoke updated title"));
            Console.WriteLine("SMOKE worker-thread update burst retained its final value through dispatcher projection.");
            await Task.Run(() =>
            {
                thread.Activity = ThreadActivity.Downloading;
                thread.ReportRequest(new RequestPolicyProgress(RequestPolicyPhase.WaitingForAdmission, null, 0));
            });
            await WaitUntilAsync(() => VisibleText(window, "Downloading · Waiting for request slot"));
            await Task.Run(() => thread.ReportRequest(new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 1)));
            await WaitUntilAsync(() => VisibleText(window, "Downloading · Request cooldown (30 s)"));
            await Task.Run(() => thread.ReportRequest(null));
            await WaitUntilAsync(() => VisibleText(window, "Downloading"));
            threadRow.UpdateSnapshot(thread.GetSnapshot(new DownloadCounts(3, 2)) with
            {
                Activity = ThreadActivity.Finished,
                LastCheckFailure = new FailureInfo(FailureKind.Timeout, "Smoke retained check failure")
            });
            if (threadRow.Status != "Check failed · Finished" || threadRow.Queued != 3 || threadRow.Active != 2
                || threadRow.ErrorDetails != "Smoke retained check failure")
                throw new InvalidOperationException("Typed queue counts or retained check failures were not projected.");
            Console.WriteLine("SMOKE request waiting/backoff rendered; typed queue counts and retained check failure projected.");
            await Task.Run(() =>
            {
                thread.MarkArchived();
                thread.Activity = ThreadActivity.Finished;
            });
            await WaitUntilAsync(() => VisibleText(window, "Archived · Finished"));
            Console.WriteLine("SMOKE terminal source state remained visible after media status changed.");
            CaptureWindow(window, "smoke-mainwindow.png");

            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedIndex = 1;
            await WaitUntilAsync(() => VisibleText(window, boardRow.NiceName));
            await Task.Run(() => board.ReportRequest(new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(12), 1)));
            await WaitUntilAsync(() => VisibleText(window, "Request cooldown (12 s)"));
            await Task.Run(() => board.ReportRequest(null));
            await WaitUntilAsync(() => VisibleText(window, "Monitoring"));
            Console.WriteLine("SMOKE board binding and request cooldown rendered.");

            string finalTitle = threadRow.Title;
            threadRow.Dispose();
            await Task.Run(() => thread.Title = "Disposed row must not update");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (threadRow.Title != finalTitle)
                throw new InvalidOperationException("A disposed row still accepted updates.");

            mainWindow.UrlList.Remove(threadRow);
            mainWindow.BoardsList.Remove(boardRow);
            await GridChecks.RunAsync(mainWindow, provider);
            await SettingsChecks.RunAsync(mainWindow);
            _checksPassed = true;
            Console.WriteLine("SMOKE requesting normal MainWindow.Close().");
            window.Close();
        }
        catch (Exception exception)
        {
            _failure = exception;
            Console.Error.WriteLine("SMOKE check failed: " + exception);
            if (window is MainWindow mainWindow && mainWindow.FindControl<MenuItem>("ExitMenuItem") is MenuItem exit)
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else if (window != null) window.Close();
            else if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(1);
        }
    }

    private static bool VisibleText(Window window, string text) => window.GetVisualDescendants()
        .OfType<TextBlock>().Any(block => block.IsVisible && block.Text == text);

    internal static void CaptureWindow(Window window, string fileName)
    {
        string screenshotPath = Path.Combine(AppContext.BaseDirectory, fileName);
        using RenderTargetBitmap screenshot = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        screenshot.Render(window);
        screenshot.Save(screenshotPath);
        Console.WriteLine("SMOKE window render: " + screenshotPath);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Desktop state did not become ready within five seconds.");
            await Task.Delay(50);
        }
    }

    private sealed class RejectRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Smoke fixture attempted a network request: " + request.RequestUri);
    }

    // Replace only tray creation; windows, rendering and the desktop lifetime stay native.
    private sealed class NoTrayPlatform : IWindowingPlatform
    {
        private readonly IWindowingPlatform _inner;
        private readonly bool _throwOnTrayCreation;

        public NoTrayPlatform(IWindowingPlatform inner, bool throwOnTrayCreation)
        {
            _inner = inner;
            _throwOnTrayCreation = throwOnTrayCreation;
        }

        public IWindowImpl CreateWindow() => _inner.CreateWindow();
        public ITopLevelImpl CreateEmbeddableTopLevel() => _inner.CreateEmbeddableTopLevel();
        public IWindowImpl CreateEmbeddableWindow() => _inner.CreateEmbeddableWindow();

        public ITrayIconImpl? CreateTrayIcon()
        {
            if (_throwOnTrayCreation)
                throw new InvalidOperationException("Smoke tray creation failure.");
            return null;
        }
    }

    private sealed class SmokeThread : FourChanThread
    {
        public SmokeThread(IBoard board, Uri threadUri) : base(board, threadUri) { }

        public void MarkArchived() => MarkPollingTerminal(ThreadSourceState.Archived);
        public void ReportRequest(RequestPolicyProgress? progress) => SetRequestProgress(progress);
    }

    private sealed class SmokeBoard : FourChanBoard
    {
        public SmokeBoard(IImageBoard provider, Uri boardUri) : base(provider, boardUri) { }
        public void ReportRequest(RequestPolicyProgress? progress) => SetRequestProgress(progress);
    }

    private sealed class SmokeLogSink : ILogSink
    {
        public ConcurrentQueue<string> Errors { get; } = new ConcurrentQueue<string>();

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Record(level, area, messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            => Record(level, area, messageTemplate + " " + string.Join(", ", propertyValues));

        private void Record(LogEventLevel level, string area, string message)
        {
            string line = $"AVALONIA [{level}] [{area}] {message}";
            Console.WriteLine(line);
            if (level >= LogEventLevel.Error || area == "Binding") Errors.Enqueue(line);
        }
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace oChan.DesktopSmoke;

internal static class SettingsChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ochan-settings-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CheckApplyAsync(owner, Path.Combine(profile, "apply"));
            await CheckOwnerCloseWhileBusyAsync(owner, Path.Combine(profile, "owner-close"));
            await CheckDirectSaveAsync(owner, Path.Combine(profile, "direct-save"));
            await CheckPickerAsync(owner, Path.Combine(profile, "picker"));
            Console.WriteLine("SMOKE settings: pending close/duplicate protection, visible errors, validation, retry, isolated save and folder-picker outcomes passed.");
        }
        finally
        {
            if (Directory.Exists(profile))
                Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckApplyAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        config.SaveConfig();
        string original = File.ReadAllText(Path.Combine(profile, "settings.json"));
        TaskCompletionSource pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task applyResult = pending.Task;
        int applies = 0;
        SettingsWindow window = new SettingsWindow(config, async candidate =>
        {
            applies++;
            await applyResult;
            candidate.SaveConfig();
        });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => window.IsVisible && window.Bounds.Width > 0);
            NumericUpDown timer = window.FindControl<NumericUpDown>("RecheckTimerInput")!;
            Require(timer.Maximum == 4294967, "Settings allowed a timer above the runtime limit.");
            window.RecheckTimer = 120;
            Button save = FindButton(window, "Save & Close");
            Click(save);
            Require(window.IsBusy && !save.IsEffectivelyEnabled && applies == 1, "Pending settings apply did not disable the form.");
            Click(save);
            Require(applies == 1, "A duplicate save entered the pending callback twice.");
            Func<WindowCloseReason, bool> requestClose = window.PlatformImpl?.Closing
                ?? throw new InvalidOperationException("Settings native close callback is missing.");
            Require(requestClose(WindowCloseReason.WindowClosing), "Native close was allowed during a pending settings apply.");
            Require(window.IsVisible && !dialog.IsCompleted, "Pending settings dialog closed before apply completed.");
            pending.SetException(new IOException("Synthetic settings apply failure."));
            await WaitUntilAsync(() => !window.IsBusy && window.HasError);
            Require(window.IsVisible && !dialog.IsCompleted, "Apply failure closed the settings dialog.");
            Require(config.RecheckTimer == 60, "Apply failure changed the live configuration.");
            Require(File.ReadAllText(Path.Combine(profile, "settings.json")) == original, "The dialog saved settings before its apply callback committed.");
            await AssertErrorInViewportAsync(window);

            Action[] invalidEdits =
            {
                () => window.RecheckTimer = null,
                () => window.RecheckTimer = 60.5m,
                () => window.MaxParallelDownloads = 1.5m,
                () => window.BandwidthLimiterMB = 0,
                () => window.BandwidthLimiterMB = 8796093022208m
            };
            foreach (Action edit in invalidEdits)
            {
                window.RecheckTimer = 120;
                window.MaxParallelDownloads = 3;
                window.BandwidthLimiterMB = 2;
                edit();
                Click(save);
                await WaitUntilAsync(() => !window.IsBusy);
                Require(window.HasError && applies == 1, "An invalid setting entered the commit callback.");
                Require(config.RecheckTimer == 60 && config.MaxParallelDownloads == 5, "An invalid setting changed the live configuration.");
            }

            window.RecheckTimer = 120;
            window.MaxParallelDownloads = 3;
            window.BandwidthLimiterMB = 2;
            applyResult = Task.CompletedTask;
            Click(save);
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(applies == 2 && !window.IsVisible && !window.IsBusy, "Successful settings retry did not close exactly once.");
            Require(config.RecheckTimer == 120 && config.MaxParallelDownloads == 3, "Successful settings apply did not update the live configuration.");
            Require(Config.LoadConfig(profile).RecheckTimer == 120, "Successful settings apply did not commit the isolated profile.");
        }
        finally
        {
            pending.TrySetCanceled();
            if (window.IsVisible)
            {
                await WaitUntilAsync(() => !window.IsBusy);
                window.Close();
            }
        }
    }

    private static async Task CheckDirectSaveAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        Directory.CreateDirectory(Path.Combine(profile, "settings.json"));
        SettingsWindow window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            window.RecheckTimer = 120;
            Button save = FindButton(window, "Save & Close");
            Click(save);
            await WaitUntilAsync(() => !window.IsBusy && window.HasError);
            Require(window.IsVisible && config.RecheckTimer == 60, "Direct save failure closed the dialog or changed live settings.");
            Require(Directory.Exists(Path.Combine(profile, "settings.json")), "Direct save failure replaced an unreadable settings path.");
            await AssertErrorInViewportAsync(window);
            Directory.Delete(Path.Combine(profile, "settings.json"));
            Click(save);
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(config.RecheckTimer == 120 && Config.LoadConfig(profile).RecheckTimer == 120, "Direct save retry failed to commit settings.");
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
        }
    }

    private static async Task CheckOwnerCloseWhileBusyAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        TaskCompletionSource pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window parent = new Window { Title = "Settings owner-close fixture", Width = 480, Height = 480 };
        parent.Show(owner);
        SettingsWindow window = new SettingsWindow(config, candidate => pending.Task);
        Task dialog = window.ShowDialog(parent);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            window.RecheckTimer = 120;
            Click(FindButton(window, "Save & Close"));
            Require(window.IsBusy, "Owner-close fixture did not enter pending settings apply.");
            parent.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!parent.IsVisible && !window.IsVisible, "A busy settings dialog blocked its owner from closing.");
            pending.SetException(new IOException("Apply finished after its owner closed."));
            await WaitUntilAsync(() => !window.IsBusy);
            Require(config.RecheckTimer == 60, "An apply failure after owner close changed live settings.");
        }
        finally
        {
            pending.TrySetCanceled();
            await WaitUntilAsync(() => !window.IsBusy);
            if (window.IsVisible)
                window.Close();
            if (parent.IsVisible)
                parent.Close();
        }
    }

    private static async Task CheckPickerAsync(MainWindow owner, string profile)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        FixtureStorageProvider provider = new FixtureStorageProvider();
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new FixtureStorageProviderFactory(provider));
        Config config = Config.LoadConfig(profile);
        string originalPath = Path.Combine(profile, "original");
        config.DownloadPath = originalPath;
        SettingsWindow window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        bool wrongThread = false;
        window.PropertyChanged += (object? sender, PropertyChangedEventArgs args) =>
        {
            if (args.PropertyName == nameof(SettingsWindow.DownloadPath) && !Dispatcher.UIThread.CheckAccess())
                wrongThread = true;
        };
        TaskCompletionSource<IReadOnlyList<IStorageFolder>> pending = new TaskCompletionSource<IReadOnlyList<IStorageFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            Button browse = FindButton(window, "Browse");
            provider.CanPickFolder = false;
            Click(browse);
            Require(window.HasError && !window.IsBusy && provider.PickerCalls == 0, "Unavailable picker was not handled without starting a dialog.");
            await AssertErrorInViewportAsync(window);

            provider.CanPickFolder = true;
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
            Click(browse);
            Require(!window.IsBusy && !window.HasError && window.DownloadPath == originalPath, "Cancelling the folder picker changed settings or reported an error.");

            provider.Result = Task.FromException<IReadOnlyList<IStorageFolder>>(new IOException("Synthetic folder picker failure."));
            Click(browse);
            Require(window.HasError && !window.IsBusy && window.DownloadPath == originalPath, "Folder picker failure was not shown without changing the path.");
            await AssertErrorInViewportAsync(window);

            provider.Result = pending.Task;
            Click(browse);
            Require(window.IsBusy && !browse.IsEffectivelyEnabled, "Pending folder picker did not disable the form.");
            int calls = provider.PickerCalls;
            Click(browse);
            Require(provider.PickerCalls == calls, "Pending folder picker was opened twice.");
            Require(window.PlatformImpl!.Closing!(WindowCloseReason.WindowClosing), "Pending folder picker allowed its parent dialog to close.");
            string selectedPath = Path.Combine(profile, "selected");
            FixtureStorageFolder selected = new FixtureStorageFolder(new Uri(selectedPath));
            await Task.Run(() => pending.SetResult(new IStorageFolder[] { selected }));
            await WaitUntilAsync(() => !window.IsBusy);
            Require(window.DownloadPath == selectedPath && !window.HasError && selected.Disposed, "Local picker result was not applied and disposed.");
            Require(!wrongThread && config.DownloadPath == originalPath, "Folder picker changed live configuration or raised bindings off the UI thread.");

            FixtureStorageFolder remote = new FixtureStorageFolder(new Uri("content://fixture/nonlocal"));
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { remote });
            Click(browse);
            Require(window.HasError && window.DownloadPath == selectedPath && remote.Disposed, "Nonlocal folder was accepted or not disposed.");
            window.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pending.TrySetCanceled();
            if (window.IsVisible)
            {
                await WaitUntilAsync(() => !window.IsBusy);
                window.Close();
            }
        }
    }

    private static async Task AssertErrorInViewportAsync(SettingsWindow window)
    {
        TextBlock error = window.FindControl<TextBlock>("SettingsError")!;
        ScrollViewer scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        await WaitUntilAsync(() =>
        {
            Point? point = error.TranslatePoint(new Point(0, 0), scroll);
            return error.IsVisible && error.Bounds.Height > 0 && point.HasValue &&
                point.Value.Y >= -1 && point.Value.Y + error.Bounds.Height <= scroll.Bounds.Height + 1;
        });
    }

    private static Button FindButton(Window window, string content) => window.GetVisualDescendants()
        .OfType<Button>().Single(button => button.Content as string == content);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Settings smoke condition was not met.");
            await Task.Delay(20);
        }
    }

    private sealed class FixtureStorageProviderFactory : IStorageProviderFactory
    {
        private readonly IStorageProvider _provider;
        public FixtureStorageProviderFactory(IStorageProvider provider) => _provider = provider;
        public IStorageProvider CreateProvider(TopLevel topLevel) => _provider;
    }

    private sealed class FixtureStorageProvider : IStorageProvider
    {
        public bool CanOpen => false;
        public bool CanSave => false;
        public bool CanPickFolder { get; set; } = true;
        public int PickerCalls { get; private set; }
        public Task<IReadOnlyList<IStorageFolder>> Result { get; set; } = Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
        public Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options)
        {
            Require(!options.AllowMultiple, "Settings picker unexpectedly allowed multiple folders.");
            PickerCalls++;
            return Result;
        }
        public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageFile?> TryGetFileFromPathAsync(Uri filePath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri folderPath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder wellKnownFolder) => throw new NotSupportedException();
    }

    private sealed class FixtureStorageFolder : IStorageFolder
    {
        public FixtureStorageFolder(Uri path) => Path = path;
        public string Name => "Fixture folder";
        public Uri Path { get; }
        public bool CanBookmark => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => throw new NotSupportedException();
        public Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public IAsyncEnumerable<IStorageItem> GetItemsAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> GetFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> CreateFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFolder?> CreateFolderAsync(string name) => throw new NotSupportedException();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace oChan.DesktopSmoke;

internal static class ClipboardChecks
{
    private static FixtureClipboard? _clipboard;

    // Avalonia X11 resolves the window clipboard through this platform service.
    // Install after platform services, before App creates any windows.
    public static void Install()
    {
        if (!OperatingSystem.IsLinux())
            return;

        _clipboard = new FixtureClipboard();
        AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(_clipboard);
    }

    public static async Task RunAsync(MainWindow window)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("SMOKE SKIP: clipboard isolation uses X11; native clipboard untested.");
            return;
        }

        FixtureClipboard clipboard = _clipboard
            ?? throw new InvalidOperationException("The isolated clipboard was not installed.");
        if (!ReferenceEquals(window.Clipboard, clipboard))
            throw new InvalidOperationException("Window clipboard isolation failed; refusing to access the desktop clipboard.");

        Button add = window.FindControl<Button>("AddUrlButton")
            ?? throw new InvalidOperationException("The Add button is missing.");
        TextBox input = window.FindControl<TextBox>("UrlInput")
            ?? throw new InvalidOperationException("The URL input is missing.");

        // Unsupported hosts are rejected during Registry admission, before any provider request.
        const string explicitUrl = "  https://unsupported.invalid/explicit  ";
        clipboard.Read = () => throw new InvalidOperationException("Explicit Add unexpectedly read the clipboard.");
        input.Text = explicitUrl;
        Click(add);
        await WaitUntilAsync(() => add.IsEnabled && Notifications(window, "Unsupported URL").Length == 1);
        if (clipboard.ReadCount != 0 || input.Text != explicitUrl)
            throw new InvalidOperationException("Explicit Add read the clipboard or discarded a rejected URL.");
        CloseNotifications(window);

        TaskCompletionSource<IAsyncDataTransfer?> clipboardRead = new TaskCompletionSource<IAsyncDataTransfer?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        clipboard.Read = () => clipboardRead.Task;
        input.Text = " ";
        Click(add);
        if (add.IsEnabled || clipboard.ReadCount != 1)
            throw new InvalidOperationException("Empty Add did not wait for exactly one clipboard read.");
        Click(add);
        if (clipboard.ReadCount != 1)
            throw new InvalidOperationException("An Add action overlapped the pending clipboard import.");
        clipboardRead.SetResult(TextData("https://unsupported.invalid/first\r\nnot a URL\n https://unsupported.invalid/second "));
        await WaitUntilAsync(() => add.IsEnabled && Notifications(window, "Unsupported URL").Length == 2);
        if (clipboard.ReadCount != 1)
            throw new InvalidOperationException("Multiline import read the clipboard more than once.");
        CloseNotifications(window);

        clipboard.Read = () => Task.FromResult<IAsyncDataTransfer?>(TextData("  \r\n "));
        input.Text = string.Empty;
        Click(add);
        await WaitUntilAsync(() => add.IsEnabled && Notifications(window, "Clipboard")
            .Any(notification => notification.Message == "No valid URLs found in the clipboard."));
        if (clipboard.ReadCount != 2)
            throw new InvalidOperationException("Empty clipboard import did not read once.");
        CloseNotifications(window);

        clipboard.Read = () => Task.FromException<IAsyncDataTransfer?>(
            new InvalidOperationException("Smoke clipboard read failure."));
        Click(add);
        await WaitUntilAsync(() => add.IsEnabled && Notifications(window, "Error")
            .Any(notification => notification.Message == "The clipboard could not be read."));
        if (clipboard.ReadCount != 3 || !input.IsEnabled || window.UrlList.Count != 0 || window.BoardsList.Count != 0)
            throw new InvalidOperationException("Clipboard failure did not leave the empty session usable.");
        CloseNotifications(window);
        clipboard.Read = () => Task.FromResult<IAsyncDataTransfer?>(null);

        Console.WriteLine("SMOKE isolated clipboard: explicit Add bypassed clipboard; empty Add imported two URLs once; overlapping Add ignored; empty/error feedback restored controls.");
    }

    private static DataTransfer TextData(string text)
    {
        DataTransfer data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.Text, text));
        return data;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static INotification[] Notifications(Window window, string title) => window.GetVisualDescendants()
        .OfType<NotificationCard>()
        .Where(card => !card.IsClosing && !card.IsClosed)
        .Select(card => card.Content)
        .OfType<INotification>()
        .Where(notification => notification.Title == title)
        .ToArray();

    private static void CloseNotifications(Window window)
    {
        foreach (NotificationCard card in window.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            card.Close();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Clipboard UI state did not become ready within five seconds.");
            await Task.Delay(25);
        }
    }

    // This fixture never delegates to a native clipboard, including for write operations.
    private sealed class FixtureClipboard : IClipboard
    {
        public int ReadCount { get; private set; }
        public Func<Task<IAsyncDataTransfer?>> Read { get; set; } = () =>
            Task.FromException<IAsyncDataTransfer?>(new InvalidOperationException("No fixture clipboard content configured."));

        public Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            ReadCount++;
            return Read();
        }

        public Task SetTextAsync(string? text) => throw UnexpectedApi();
        public Task ClearAsync() => throw UnexpectedApi();
        public Task SetDataAsync(IAsyncDataTransfer? dataTransfer) => throw UnexpectedApi();
        public Task FlushAsync() => throw UnexpectedApi();
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => throw UnexpectedApi();

        [Obsolete("The smoke fixture only supports TryGetDataAsync.")]
        public Task<string?> GetTextAsync() => throw UnexpectedApi();

        [Obsolete("The smoke fixture only supports TryGetDataAsync.")]
        public Task SetDataObjectAsync(IDataObject data) => throw UnexpectedApi();

        [Obsolete("The smoke fixture only supports TryGetDataAsync.")]
        public Task<string[]> GetFormatsAsync() => throw UnexpectedApi();

        [Obsolete("The smoke fixture only supports TryGetDataAsync.")]
        public Task<object?> GetDataAsync(string format) => throw UnexpectedApi();

        [Obsolete("The smoke fixture only supports TryGetDataAsync.")]
        public Task<IDataObject?> TryGetInProcessDataObjectAsync() => throw UnexpectedApi();

        private static InvalidOperationException UnexpectedApi() =>
            new InvalidOperationException("The smoke fixture received an unexpected clipboard API call.");
    }
}

using System;
using System.Threading.Tasks;

namespace oChan.Desktop;

/// <summary>Resolves one Add action without reading the clipboard for explicit input.</summary>
public static class AddInput
{
    public static async Task SubmitAsync(string? input, Func<Task<string?>> readClipboardAsync, Func<string, Task> admitAsync)
    {
        if (!string.IsNullOrWhiteSpace(input))
        {
            await admitAsync(input.Trim());
            return;
        }

        string? clipboardText = await readClipboardAsync();
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            return;
        }

        string[] lines = clipboardText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            string url = line.Trim();
            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                await admitAsync(url);
            }
        }
    }
}

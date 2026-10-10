using oChan.Desktop;

namespace oChan.DesktopTests;

public sealed class AddInputTests
{
    [Fact]
    public async Task TextInputIsAdmittedOnceWithoutReadingClipboard()
    {
        const string input = "https://input.test/g/thread/123";
        int clipboardReads = 0;
        List<string> admitted = new List<string>();

        await AddInput.SubmitAsync(
            $"  {input} \t",
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>("https://clipboard.test/g/thread/456");
            },
            url =>
            {
                admitted.Add(url);
                return Task.CompletedTask;
            });

        Assert.Equal(0, clipboardReads);
        Assert.Equal(input, Assert.Single(admitted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task EmptyInputReadsClipboardOnceAndImportsOneUrl(string? input)
    {
        const string clipboardUrl = "https://clipboard.test/g/thread/456";
        int clipboardReads = 0;
        List<string> admitted = new List<string>();

        await AddInput.SubmitAsync(
            input,
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>($" \r\n  {clipboardUrl}  \n");
            },
            url =>
            {
                admitted.Add(url);
                return Task.CompletedTask;
            });

        Assert.Equal(1, clipboardReads);
        Assert.Equal(clipboardUrl, Assert.Single(admitted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task EmptyClipboardDoesNotAdmitAnything(string? clipboardText)
    {
        int clipboardReads = 0;
        int admissions = 0;

        await AddInput.SubmitAsync(
            string.Empty,
            () =>
            {
                clipboardReads++;
                return Task.FromResult(clipboardText);
            },
            _ =>
            {
                admissions++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, clipboardReads);
        Assert.Equal(0, admissions);
    }

    [Fact]
    public async Task ClipboardImportAwaitsEachAdmissionBeforeStartingTheNext()
    {
        const string first = "https://clipboard.test/g/thread/123";
        const string second = "https://clipboard.test/g/thread/456";
        TaskCompletionSource firstAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> admitted = new List<string>();

        Task submission = AddInput.SubmitAsync(
            string.Empty,
            () => Task.FromResult<string?>($" {first} \r\ninvalid URL\r{second}\n"),
            url =>
            {
                admitted.Add(url);
                if (url != first)
                    return Task.CompletedTask;

                firstStarted.SetResult();
                return firstAdmission.Task;
            });

        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(submission.IsCompleted);
            Assert.Equal(first, Assert.Single(admitted));
        }
        finally
        {
            firstAdmission.TrySetResult();
        }

        await submission.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { first, second }, admitted);
    }
}

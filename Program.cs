using Avalonia;
using System;
using Serilog;

namespace oChan;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The desktop host owns logging; constructing a library session never changes it.
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateLogger();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { Log.CloseAndFlush(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

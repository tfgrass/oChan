using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using oChan.Desktop;
using Serilog;
using System;

namespace oChan;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = new MainWindow();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Desktop startup failed.");
                desktop.MainWindow = new StartupErrorWindow(exception.Message);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

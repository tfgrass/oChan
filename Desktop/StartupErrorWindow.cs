using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace oChan.Desktop;

/// <summary>Keeps profile and initialization failures visible even when MainWindow cannot be created.</summary>
public sealed class StartupErrorWindow : Window
{
    public StartupErrorWindow(string message)
    {
        Title = "oChan could not start";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Button exitButton = new Button
        {
            Content = "Exit",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 90
        };
        exitButton.Click += (sender, e) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "oChan could not start", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new ScrollViewer
                {
                    MaxHeight = 280,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
                },
                exitButton
            }
        };
    }
}

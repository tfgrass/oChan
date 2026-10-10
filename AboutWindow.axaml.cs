using Avalonia.Controls;
using Avalonia.Input;
using System.Diagnostics;
using System.Reflection;

namespace oChan
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            Assembly assembly = typeof(AboutWindow).Assembly;
            string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string version = informationalVersion?.Split('+', 2)[0]
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            VersionText.Text = $"Version {version}";
        }

        private void OnLinkPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.Tag is string uri)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
            }
        }

        private void OnCloseButtonClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            this.Close();
        }
    }
}

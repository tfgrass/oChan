using Avalonia.Controls;
using Avalonia.Input;
using System.Diagnostics;

namespace oChan
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
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

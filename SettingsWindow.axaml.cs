// File: SettingsWindow.axaml.cs

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace oChan
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

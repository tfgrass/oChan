// File: AboutWindow.axaml.cs

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace oChan
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

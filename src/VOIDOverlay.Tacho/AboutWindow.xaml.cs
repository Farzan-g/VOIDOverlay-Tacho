using System.Windows;

namespace VOIDOverlay.Tacho;

public partial class AboutWindow : Window
{
    public AboutWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Version: {version}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

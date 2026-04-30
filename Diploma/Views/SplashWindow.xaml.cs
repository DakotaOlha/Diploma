using System.Windows;

namespace Diploma.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    public void SetStatus(string message) => StatusText.Text = message;
}
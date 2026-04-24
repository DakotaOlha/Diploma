using System.Windows;

namespace Diploma.Views;

public partial class MarkerDialog : Window
{
    public string MarkerText { get; private set; } = string.Empty;
    
    public MarkerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => MarkerTextBox.Focus();
    }
    
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var text = MarkerTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        MarkerText = text;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
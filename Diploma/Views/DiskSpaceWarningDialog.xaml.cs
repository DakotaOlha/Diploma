using System.Windows;
using Diploma.Core.Services;

namespace Diploma.Views;

public partial class DiskSpaceWarningDialog : Window
{
    public DiskSpaceWarningDialog(DiskSpaceCheckResult result, DiskSpaceService diskService)
    {
        InitializeComponent();

        FreeSpaceText.Text =
            $"Вільно на диску {result.DriveName}: " +
            $"{diskService.FormatFreeSpace(result.FreeBytes)}";

        EstimatedText.Text = result.EstimatedHours < 0.1
            ? "Місця вистачить менш ніж на кілька хвилин запису!"
            : $"При поточному бітрейті вистачить приблизно на " +
              $"{result.EstimatedHours:F1} год. запису.";

        if (result.EstimatedHours < 0.1)
            EstimatedText.Foreground = System.Windows.Media.Brushes.Red;
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
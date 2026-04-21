using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Diploma.Core.Interfaces;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class OverlayWindow : Window
{
    private readonly IScreenCaptureService _captureService;
    private readonly IAudioCaptureService _audioService;

    private DateTime? _videoStartTime;
    private DateTime? _audioStartTime;

    private static readonly SolidColorBrush GreenBrush =
        new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush GrayBrush =
        new(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly SolidColorBrush RedBrush =
        new(Color.FromRgb(0xFF, 0x55, 0x55));

    public OverlayWindow(MainViewModel viewModel,
                         IScreenCaptureService captureService,
                         IAudioCaptureService audioService)
    {
        InitializeComponent();
        DataContext = viewModel;

        _captureService = captureService;
        _audioService = audioService;

        _captureService.RecordingStarted += (_, _) =>
        {
            _videoStartTime = DateTime.Now;
            _audioStartTime = DateTime.Now;
    
            App.Current.Dispatcher.Invoke(() =>
            {
                VideoStatusDot.Foreground = GreenBrush;
                AudioStatusDot.Foreground = GreenBrush;
            });
        };

        var timer = new System.Timers.Timer(200);
        timer.Elapsed += (_, _) => App.Current.Dispatcher.Invoke(UpdateTimers);
        timer.Start();
    }

    private void UpdateTimers()
    {
        var now = DateTime.Now;

        if (_videoStartTime.HasValue && _captureService.IsRecording)
        {
            var videoElapsed = now - _videoStartTime.Value;
            VideoTimerText.Text = videoElapsed.ToString(@"hh\:mm\:ss\.f");
            VideoTimerText.Foreground = GreenBrush;
            VideoStatusDot.Foreground = GreenBrush;
        }
        else
        {
            VideoTimerText.Text = "00:00:00.0";
            VideoTimerText.Foreground = GrayBrush;
            VideoStatusDot.Foreground = GrayBrush;
        }

        if (_audioStartTime.HasValue && _audioService.IsRecording)
        {
            var audioElapsed = now - _audioStartTime.Value;
            AudioTimerText.Text = audioElapsed.ToString(@"hh\:mm\:ss\.f");
            AudioTimerText.Foreground = GreenBrush;
            AudioStatusDot.Foreground = GreenBrush;
        }
        else
        {
            AudioTimerText.Text = "00:00:00.0";
            AudioTimerText.Foreground = GrayBrush;
            AudioStatusDot.Foreground = GrayBrush;
        }

        if (_videoStartTime.HasValue && _audioStartTime.HasValue
            && _captureService.IsRecording && _audioService.IsRecording)
        {
            var videoElapsed = (now - _videoStartTime.Value).TotalSeconds;
            var audioElapsed = (now - _audioStartTime.Value).TotalSeconds;
            var delta = videoElapsed - audioElapsed;

            DeltaText.Text = $"Δ {delta:+0.0;-0.0;0.0}s " +
                             (delta > 0 ? "(audio lags)" :
                              delta < 0 ? "(video lags)" : "(in sync)");

            DeltaText.Foreground = Math.Abs(delta) > 1.0 ? RedBrush : GrayBrush;
        }
        else
        {
            DeltaText.Text = "Δ —";
            DeltaText.Foreground = GrayBrush;
        }

        if (!_captureService.IsRecording && _videoStartTime.HasValue)
        {
            _videoStartTime = null;
            _audioStartTime = null;
        }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();
}
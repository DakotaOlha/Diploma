using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Diploma.Core.Models;
using Diploma.Helpers;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class PlayerView : UserControl
{
    private readonly PlayerViewModel _vm;
    private bool _isDragging;

    private static readonly EventTypeToColorConverter _colorConverter = new();

    public PlayerView(PlayerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;

        Loaded += (_, _) =>
        {
            VideoView.MediaPlayer = _vm.MediaPlayer;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Entries.CollectionChanged += OnEntriesCollectionChanged;

            TimelineCanvas.MouseLeftButtonDown += OnCanvasMouseLeftButtonDown;
        };

        SeekSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _isDragging = true));
    }

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var x        = e.GetPosition(TimelineCanvas).X;
        var width    = TimelineCanvas.ActualWidth;
        var duration = _vm.DurationMs;

        if (width <= 0 || duration <= 0) return;

        var ratio  = Math.Clamp(x / width, 0.0, 1.0);
        var timeMs = (long)(ratio * duration);
        _vm.SeekCommand.Execute(timeMs);

        e.Handled = true;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.ActiveEntry):
                if (_vm.ActiveEntry is not null)
                    EntriesGrid.ScrollIntoView(_vm.ActiveEntry);
                break;

            case nameof(PlayerViewModel.PositionMs):
                UpdatePlayhead();
                break;

            case nameof(PlayerViewModel.DurationMs):
                RefreshMarkers();
                break;
        }
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshMarkers();

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshMarkers();

    private void RefreshMarkers()
    {
        TimelineCanvas.Children.Clear();

        TimelineCanvas.Children.Add(Playhead);

        var width    = TimelineCanvas.ActualWidth;
        var duration = _vm.DurationMs;

        if (width <= 0 || duration <= 0)
            return;

        const double MarkerWidth = 10.0;
        const double MarkerHalf  = MarkerWidth / 2.0;

        foreach (var entry in _vm.Entries)
        {
            var ratio = Math.Clamp(entry.Offset.TotalMilliseconds / duration, 0.0, 1.0);
            var left  = Math.Clamp(ratio * width - MarkerHalf, 0.0, width - MarkerWidth);

            var brush = (Brush)_colorConverter.Convert(
                entry.EventType, typeof(Brush), null,
                System.Globalization.CultureInfo.InvariantCulture);

            var marker = new Grid
            {
                Width            = MarkerWidth,
                Height           = 36,
                Cursor           = Cursors.Hand,
                Tag              = entry,
                ToolTip          = BuildTooltip(entry),
                IsHitTestVisible = true,
                Background       = Brushes.Transparent,
            };
            ToolTipService.SetInitialShowDelay(marker, 150);

            marker.Children.Add(new Rectangle
            {
                Width               = 2,
                VerticalAlignment   = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill                = brush,
                Opacity             = 0.7,
            });

            marker.Children.Add(new Ellipse
            {
                Width               = 8,
                Height              = 8,
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 1, 0, 0),
                Fill                = brush,
            });

            marker.MouseEnter += (_, _) =>
            {
                foreach (UIElement child in marker.Children)
                    if (child is Shape s) s.Opacity = 1.0;
            };
            marker.MouseLeave += (_, _) =>
            {
                foreach (UIElement child in marker.Children)
                    if (child is Shape s) s.Opacity = child is Rectangle ? 0.7 : 1.0;
            };
            marker.MouseLeftButtonDown += (_, args) =>
            {
                if (marker.Tag is LogEntry e)
                    _vm.JumpToEntryCommand.Execute(e);
                args.Handled = true;
            };

            Canvas.SetLeft(marker, left);
            Canvas.SetTop(marker, 0);
            TimelineCanvas.Children.Add(marker);
        }
    }

    private static ToolTip BuildTooltip(LogEntry entry)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text       = entry.EventType,
            FontWeight = FontWeights.SemiBold,
            FontSize   = 11,
        });
        sp.Children.Add(new TextBlock
        {
            Text         = entry.Description,
            FontSize     = 11,
            Opacity      = 0.85,
            MaxWidth     = 260,
            TextWrapping = TextWrapping.Wrap,
        });
        sp.Children.Add(new TextBlock
        {
            Text     = $"⏱ {entry.Offset:mm\\:ss\\.f}",
            FontSize = 10,
            Opacity  = 0.6,
        });
        return new ToolTip { Content = sp };
    }

    private void UpdatePlayhead()
    {
        var duration = _vm.DurationMs;
        if (duration <= 0) return;
        PlayheadTranslate.X = (double)_vm.PositionMs / duration * TimelineCanvas.ActualWidth;
    }

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDragging = false;
        _vm.SeekCommand.Execute((long)SeekSlider.Value);
    }

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            _vm.SeekCommand.Execute((long)SeekSlider.Value);
    }
}
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class PlayerView : UserControl
{
    private readonly PlayerViewModel _vm;
    private bool _isDragging;

    public PlayerView(PlayerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;

        Loaded += (_, _) =>
        {
            VideoView.MediaPlayer = _vm.MediaPlayer;

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.ActiveEntry)
                    && _vm.ActiveEntry is not null)
                {
                    EntriesGrid.ScrollIntoView(_vm.ActiveEntry);
                }
            };
        };

        SeekSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _isDragging = true));
    }

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDragging = false;
        _vm.SeekCommand.Execute((long)SeekSlider.Value);
        
        Loaded += (_, _) =>
        {
            if (VideoView.MediaPlayer == null)
                VideoView.MediaPlayer = _vm.MediaPlayer;

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.ActiveEntry)
                    && _vm.ActiveEntry is not null)
                {
                    EntriesGrid.ScrollIntoView(_vm.ActiveEntry);
                }
            };
        };
    }

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            _vm.SeekCommand.Execute((long)SeekSlider.Value);
    }
}
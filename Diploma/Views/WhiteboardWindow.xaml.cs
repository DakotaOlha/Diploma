using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Diploma.Views;

public partial class WhiteboardWindow : Window
{
    private enum Tool { Pen, Text, Rect, Eraser }

    private readonly record struct UndoEntry
    {
        public Stroke?    Stroke  { get; init; }
        public UIElement? Element { get; init; }

        public static UndoEntry ForStroke (Stroke    s) => new() { Stroke  = s };
        public static UndoEntry ForElement(UIElement e) => new() { Element = e };
    }

    private readonly Stack<UndoEntry> _undoStack = new();

    private Tool   _activeTool  = Tool.Pen;
    private Color  _activeColor = Colors.Black;
    private double _thickness   = 2.0;

    private Point  _dragStart;
    private bool   _isDragging;
    private Shape? _previewShape;

    private TextBox? _activeTextBox;

    private bool _isFullscreen;
    private Rect _normalBounds;

    private static readonly Color[] Palette =
    [
        Colors.Black, Color.FromRgb(0x44, 0x44, 0x44),
        Colors.Red,   Color.FromRgb(0x1A, 0x78, 0xC2),
        Color.FromRgb(0x22, 0x8B, 0x22), Colors.Orange,
        Color.FromRgb(0x80, 0x00, 0x80),
    ];

    public WhiteboardWindow()
    {
        InitializeComponent();

        ColorPicker.ItemsSource = Palette;

        DrawingCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color      = _activeColor,
            Width      = _thickness,
            Height     = _thickness,
            FitToCurve = true,
        };

        DrawingCanvas.Strokes.StrokesChanged += OnStrokesChanged;
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        foreach (var stroke in e.Added)
            _undoStack.Push(UndoEntry.ForStroke(stroke));
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoLast();

    private void UndoLast()
    {
        while (_undoStack.TryPop(out var entry))
        {
            if (entry.Stroke is { } stroke)
            {
                if (!DrawingCanvas.Strokes.Contains(stroke)) continue;
                DrawingCanvas.Strokes.StrokesChanged -= OnStrokesChanged;
                DrawingCanvas.Strokes.Remove(stroke);
                DrawingCanvas.Strokes.StrokesChanged += OnStrokesChanged;
                return;
            }
            if (entry.Element is { } element)
            {
                if (!DrawingCanvas.Children.Contains(element)) continue;
                DrawingCanvas.Children.Remove(element);
                return;
            }
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e) => ClearAll();

    private void ClearAll()
    {
        _activeTextBox = null;
        DrawingCanvas.Strokes.StrokesChanged -= OnStrokesChanged;
        DrawingCanvas.Strokes.Clear();
        DrawingCanvas.Strokes.StrokesChanged += OnStrokesChanged;
        DrawingCanvas.Children.Clear();
        _undoStack.Clear();
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var tool = btn.Tag switch
        {
            "Pen"    => Tool.Pen,
            "Text"   => Tool.Text,
            "Rect"   => Tool.Rect,
            "Eraser" => Tool.Eraser,
            _        => Tool.Pen
        };
        SelectTool(tool);
    }

    private void SelectTool(Tool tool)
    {
        if (_activeTextBox is not null) CommitActiveTextBox();

        _activeTool = tool;

        foreach (var b in new[] { BtnPen, BtnText, BtnRect, BtnEraser })
            b.Style = (Style)Resources["ToolBtn"];

        var activeBtn = tool switch
        {
            Tool.Pen    => BtnPen,
            Tool.Text   => BtnText,
            Tool.Rect   => BtnRect,
            Tool.Eraser => BtnEraser,
            _           => BtnPen
        };
        activeBtn.Style = (Style)Resources["ToolBtnActive"];

        DrawingCanvas.EditingMode = tool switch
        {
            Tool.Pen    => InkCanvasEditingMode.Ink,
            Tool.Eraser => InkCanvasEditingMode.EraseByPoint,
            _           => InkCanvasEditingMode.None
        };

        DrawingCanvas.Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            { CommitActiveTextBox(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            { CancelActiveTextBox(); e.Handled = true; }
            return;
        }

        switch (e.Key)
        {
            case Key.P when Keyboard.Modifiers == ModifierKeys.None:         SelectTool(Tool.Pen);    e.Handled = true; break;
            case Key.T when Keyboard.Modifiers == ModifierKeys.None:         SelectTool(Tool.Text);   e.Handled = true; break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.None:         SelectTool(Tool.Rect);   e.Handled = true; break;
            case Key.E when Keyboard.Modifiers == ModifierKeys.None:         SelectTool(Tool.Eraser); e.Handled = true; break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:      UndoLast();              e.Handled = true; break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.Control: ClearAll();              e.Handled = true; break;
            case Key.F11:                                                     ToggleFullscreen();      e.Handled = true; break;
        }
    }

    private void DrawingCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var newThickness = Math.Clamp(_thickness + (e.Delta > 0 ? 1.0 : -1.0), 1.0, 20.0);
        if (Math.Abs(newThickness - _thickness) < 0.01) { e.Handled = true; return; }
        _thickness = newThickness;
        ThicknessSlider.Value = _thickness;
        ApplyThickness();
        e.Handled = true;
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color c })
        {
            _activeColor = c;
            DrawingCanvas.DefaultDrawingAttributes.Color = c;
        }
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _thickness = e.NewValue;
        ApplyThickness();
    }

    private void ApplyThickness()
    {
        if (DrawingCanvas is null) return;
        DrawingCanvas.DefaultDrawingAttributes.Width  = _thickness;
        DrawingCanvas.DefaultDrawingAttributes.Height = _thickness;
    }

    private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool is Tool.Pen or Tool.Eraser) return;
        var pos = e.GetPosition(DrawingCanvas);
        if (_activeTool == Tool.Text) Text_MouseDown(pos);
        else                          Drawing_MouseDown(pos);
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _previewShape is null) return;
        UpdateShape(_previewShape, _dragStart, e.GetPosition(DrawingCanvas));
    }

    private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e) => Drawing_MouseUp();

    private void Drawing_MouseDown(Point pos)
    {
        _dragStart    = pos;
        _isDragging   = true;
        _previewShape = new Rectangle
        {
            Stroke          = new SolidColorBrush(_activeColor),
            StrokeThickness = _thickness,
            Fill            = Brushes.Transparent,
        };
        DrawingCanvas.Children.Add(_previewShape);
        DrawingCanvas.CaptureMouse();
    }

    private void Drawing_MouseUp()
    {
        if (!_isDragging) return;
        _isDragging = false;
        DrawingCanvas.ReleaseMouseCapture();
        if (_previewShape is not null && (_previewShape.Width > 2 || _previewShape.Height > 2))
            _undoStack.Push(UndoEntry.ForElement(_previewShape));
        _previewShape = null;
    }

    private static void UpdateShape(Shape shape, Point from, Point to)
    {
        InkCanvas.SetLeft(shape, Math.Min(from.X, to.X));
        InkCanvas.SetTop (shape, Math.Min(from.Y, to.Y));
        shape.Width  = Math.Abs(to.X - from.X);
        shape.Height = Math.Abs(to.Y - from.Y);
    }

    private void Text_MouseDown(Point pos)
    {
        var tb = BuildTextBox();
        InkCanvas.SetLeft(tb, pos.X);
        InkCanvas.SetTop (tb, pos.Y);
        DrawingCanvas.Children.Add(tb);
        ActivateTextBox(tb);
    }

    private TextBox BuildTextBox()
    {
        var tb = new TextBox
        {
            AcceptsReturn   = true,
            AcceptsTab      = false,
            TextWrapping    = TextWrapping.Wrap,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush     = new SolidColorBrush(_activeColor),
            Foreground      = new SolidColorBrush(_activeColor),
            CaretBrush      = new SolidColorBrush(_activeColor),
            FontSize        = Math.Max(14, _thickness * 5),
            MinWidth        = 80,
            MaxWidth        = DrawingCanvas.ActualWidth > 0 ? DrawingCanvas.ActualWidth * 0.8 : 500,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        tb.TextChanged    += (_, _) => tb.Height = double.NaN;
        tb.PreviewKeyDown += TextBox_PreviewKeyDown;
        tb.LostFocus      += TextBox_LostFocus;
        return tb;
    }

    private void ActivateTextBox(TextBox tb)
    {
        if (_activeTextBox is not null && _activeTextBox != tb) CommitActiveTextBox();
        _activeTextBox = tb;
        tb.Focus();
        Keyboard.Focus(tb);
        tb.CaretIndex = tb.Text.Length;
    }

    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        { CommitActiveTextBox(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        { CancelActiveTextBox(); e.Handled = true; }
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && _activeTextBox == tb) CommitActiveTextBox();
    }

    private void CommitActiveTextBox()
    {
        if (_activeTextBox is null) return;
        var tb = _activeTextBox;
        _activeTextBox     = null;
        tb.BorderThickness = new Thickness(0);
        if (string.IsNullOrWhiteSpace(tb.Text))
            DrawingCanvas.Children.Remove(tb);
        else if (!_undoStack.Any(u => u.Element == tb))
            _undoStack.Push(UndoEntry.ForElement(tb));
        Keyboard.Focus(this);
    }

    private void CancelActiveTextBox()
    {
        if (_activeTextBox is null) return;
        var tb = _activeTextBox;
        _activeTextBox = null;
        DrawingCanvas.Children.Remove(tb);
        Keyboard.Focus(this);
    }

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            Left   = _normalBounds.Left;
            Top    = _normalBounds.Top;
            Width  = _normalBounds.Width;
            Height = _normalBounds.Height;
            FullscreenIcon.Text = "";
            _isFullscreen = false;
        }
        else
        {
            _normalBounds = new Rect(Left, Top, Width, Height);
            var wa = SystemParameters.WorkArea;
            Left   = wa.Left;
            Top    = wa.Top;
            Width  = wa.Width;
            Height = wa.Height;
            FullscreenIcon.Text = "";
            _isFullscreen = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DrawingCanvas.Strokes.StrokesChanged -= OnStrokesChanged;
        _undoStack.Clear();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

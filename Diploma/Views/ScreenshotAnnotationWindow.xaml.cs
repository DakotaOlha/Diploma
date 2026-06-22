using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Diploma.Views;

public partial class ScreenshotAnnotationWindow : Window
{
    private enum Tool
    {
        Select,     // [S]
        Arrow,      // [A]
        Rect,       // [R]
        Pen,        // [P]
        Text,       // [T]
        Highlight,  // [M]
        Eraser,     // [E]
    }

    private readonly record struct UndoEntry
    {
        public Stroke?    Stroke  { get; init; }
        public UIElement? Element { get; init; }

        public static UndoEntry ForStroke (Stroke    s) => new() { Stroke  = s };
        public static UndoEntry ForElement(UIElement e) => new() { Element = e };
    }

    private readonly Stack<UndoEntry> _undoStack = new();

    private Tool   _activeTool  = Tool.Select;
    private Color  _activeColor = Colors.Red;
    private double _thickness   = 2.0;

    private Point  _dragStart;
    private bool   _isDragging;
    private Shape? _previewShape;

    private TextBox? _activeTextBox;

    private UIElement? _selectedElement;
    private Border?    _selectionBorder;

    private bool   _isMoving;
    private Point  _moveOrigin;
    private double _moveStartLeft;
    private double _moveStartTop;

    private const double DragThreshold = 5.0;
    private bool _moveBeyondThreshold;

    private readonly string _outputPath;

    private static readonly Color[] Palette =
    [
        Colors.Red, Color.FromRgb(0xFF, 0xA0, 0x00),
        Colors.Yellow, Colors.LimeGreen,
        Colors.DeepSkyBlue, Colors.White, Colors.Black
    ];

    public ScreenshotAnnotationWindow(BitmapSource screenshot, string outputPath)
    {
        InitializeComponent();

        _outputPath = outputPath;

        SourceImage.Source = screenshot;
        Width  = Math.Min(screenshot.PixelWidth  + 2,  SystemParameters.PrimaryScreenWidth  - 80);
        Height = Math.Min(screenshot.PixelHeight + 84, SystemParameters.PrimaryScreenHeight - 80);

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

                if (_selectedElement == element)
                    ClearSelection();

                DrawingCanvas.Children.Remove(element);
                return;
            }
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var tool = btn.Tag switch
        {
            "Select"    => Tool.Select,
            "Arrow"     => Tool.Arrow,
            "Rect"      => Tool.Rect,
            "Pen"       => Tool.Pen,
            "Text"      => Tool.Text,
            "Highlight" => Tool.Highlight,
            "Eraser"    => Tool.Eraser,
            _           => Tool.Select
        };

        SelectTool(tool);
    }

    private void SelectTool(Tool tool)
    {
        if (_activeTool == Tool.Select && tool != Tool.Select)
            ClearSelection();

        if (_activeTextBox is not null)
            CommitActiveTextBox();

        _activeTool = tool;

        foreach (var b in new[] { BtnSelect, BtnArrow, BtnRect, BtnPen, BtnText, BtnHighlight, BtnEraser })
            b.Style = (Style)Resources["ToolBtn"];

        var activeBtn = tool switch
        {
            Tool.Select    => BtnSelect,
            Tool.Arrow     => BtnArrow,
            Tool.Rect      => BtnRect,
            Tool.Pen       => BtnPen,
            Tool.Text      => BtnText,
            Tool.Highlight => BtnHighlight,
            Tool.Eraser    => BtnEraser,
            _              => BtnSelect
        };
        activeBtn.Style = (Style)Resources["ToolBtnActive"];

        DrawingCanvas.EditingMode = tool switch
        {
            Tool.Pen    => InkCanvasEditingMode.Ink,
            Tool.Eraser => InkCanvasEditingMode.EraseByPoint,
            _           => InkCanvasEditingMode.None
        };

        DrawingCanvas.Cursor = tool switch
        {
            Tool.Select => Cursors.Arrow,
            Tool.Text   => Cursors.IBeam,
            _           => Cursors.Cross,
        };
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelActiveTextBox();
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.S when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Select);    e.Handled = true; break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Arrow);     e.Handled = true; break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Pen);       e.Handled = true; break;
            case Key.M when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Highlight); e.Handled = true; break;
            case Key.T when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Text);      e.Handled = true; break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Rect);      e.Handled = true; break;
            case Key.E when Keyboard.Modifiers == ModifierKeys.None:    SelectTool(Tool.Eraser);    e.Handled = true; break;
            case Key.Escape:                                            ClearSelection();            e.Handled = true; break;
            case Key.Delete:                                            DeleteSelected();            e.Handled = true; break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control: UndoLast();                  e.Handled = true; break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control: SaveAndClose();              e.Handled = true; break;
        }
    }

    private void DrawingCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        var delta        = e.Delta > 0 ? 1.0 : -1.0;
        var newThickness = Math.Clamp(_thickness + delta, 1.0, 20.0);

        if (Math.Abs(newThickness - _thickness) < 0.01) { e.Handled = true; return; }

        _thickness            = newThickness;
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

        switch (_activeTool)
        {
            case Tool.Select:
                Select_MouseDown(e, pos, sourceElement: null);
                return;

            case Tool.Text:
                Text_MouseDown(pos);
                return;

            default:
                Drawing_MouseDown(pos);
                return;
        }
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        switch (_activeTool)
        {
            case Tool.Select:
                Select_MouseMove(e.GetPosition(DrawingCanvas));
                return;

            default:
                if (!_isDragging || _previewShape is null) return;
                UpdateShape(_previewShape, _activeTool, _dragStart, e.GetPosition(DrawingCanvas));
                return;
        }
    }

    private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        switch (_activeTool)
        {
            case Tool.Select:
                Select_MouseUp(e);
                return;

            default:
                Drawing_MouseUp();
                return;
        }
    }

    private void Drawing_MouseDown(Point pos)
    {
        _dragStart  = pos;
        _isDragging = true;
        DrawingCanvas.CaptureMouse();

        _previewShape = CreateShape(_activeTool, pos, pos);
        if (_previewShape is not null)
            DrawingCanvas.Children.Add(_previewShape);
    }

    private void Drawing_MouseUp()
    {
        if (!_isDragging) return;

        _isDragging = false;
        DrawingCanvas.ReleaseMouseCapture();

        if (_previewShape is not null && IsShapeVisible(_previewShape))
            _undoStack.Push(UndoEntry.ForElement(_previewShape));

        _previewShape = null;
    }

    private void Text_MouseDown(Point pos)
    {
        var hit = FindHitElement(pos);
        if (hit is TextBox existingTb)
        {
            ActivateTextBox(existingTb);
            return;
        }

        var tb = BuildTextBox();
        InkCanvas.SetLeft(tb, pos.X);
        InkCanvas.SetTop (tb, pos.Y);
        DrawingCanvas.Children.Add(tb);
        ActivateTextBox(tb);
    }

    private void Select_MouseDown(MouseButtonEventArgs e, Point pos, UIElement? sourceElement)
    {
        if (_activeTextBox is not null && sourceElement != _activeTextBox)
            CommitActiveTextBox();

        var target = sourceElement ?? FindHitElement(pos);

        if (target is null)
        {
            ClearSelection();
            return;
        }

        _isMoving            = true;
        _moveBeyondThreshold = false;
        _moveOrigin          = pos;
        _moveStartLeft       = InkCanvas.GetLeft(target);
        _moveStartTop        = InkCanvas.GetTop(target);

        if (double.IsNaN(_moveStartLeft)) _moveStartLeft = 0;
        if (double.IsNaN(_moveStartTop))  _moveStartTop  = 0;

        SetSelection(target);

        DrawingCanvas.Cursor = Cursors.SizeAll;
        DrawingCanvas.CaptureMouse();

        e.Handled = true;
    }

    private void Select_MouseMove(Point pos)
    {
        if (!_isMoving || _selectedElement is null) return;

        var delta = pos - _moveOrigin;

        if (!_moveBeyondThreshold)
        {
            if (delta.Length < DragThreshold) return;
            _moveBeyondThreshold = true;
        }

        InkCanvas.SetLeft(_selectedElement, _moveStartLeft + delta.X);
        InkCanvas.SetTop (_selectedElement, _moveStartTop  + delta.Y);

        UpdateSelectionBorderPosition();
    }

    private void Select_MouseUp(MouseButtonEventArgs e)
    {
        if (!_isMoving) return;

        _isMoving = false;
        DrawingCanvas.ReleaseMouseCapture();
        DrawingCanvas.Cursor = Cursors.Arrow;

        if (!_moveBeyondThreshold && _selectedElement is not null)
        {
            if (e.ClickCount == 2 && _selectedElement is TextBox tb)
            {
                ClearSelection();
                ActivateTextBox(tb);
            }
        }

        _moveBeyondThreshold = false;
    }

    private void AttachSelectHandlers(TextBox tb)
    {
        tb.PreviewMouseDown += TextBox_PreviewMouseDown;
        tb.PreviewMouseMove += TextBox_PreviewMouseMove;
        tb.PreviewMouseUp   += TextBox_PreviewMouseUp;
    }

    private void TextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool != Tool.Select) return;

        var tb  = (TextBox)sender;
        var pos = e.GetPosition(DrawingCanvas);

        Select_MouseDown(e, pos, sourceElement: tb);
    }

    private void TextBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_activeTool != Tool.Select) return;
        if (!_isMoving) return;

        Select_MouseMove(e.GetPosition(DrawingCanvas));
        e.Handled = true;
    }

    private void TextBox_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool != Tool.Select) return;
        if (!_isMoving) return;

        Select_MouseUp(e);
        e.Handled = true;
    }

    private UIElement? FindHitElement(Point pos)
    {
        UIElement? found = null;

        VisualTreeHelper.HitTest(
            DrawingCanvas,
            null,
            result =>
            {
                var candidate = FindDirectChild(DrawingCanvas, result.VisualHit);
                if (candidate is not null && candidate != _selectionBorder)
                {
                    found = candidate;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(pos));

        return found;
    }

    private static UIElement? FindDirectChild(InkCanvas parent, DependencyObject? child)
    {
        var current = child;
        while (current is not null)
        {
            if (current is UIElement el && parent.Children.Contains(el))
                return el;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void SetSelection(UIElement element)
    {
        ClearSelection();

        _selectedElement = element;

        var left   = InkCanvas.GetLeft(element);
        var top    = InkCanvas.GetTop(element);
        var fe     = element as FrameworkElement;
        var width  = fe?.ActualWidth  ?? 0;
        var height = fe?.ActualHeight ?? 0;

        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top))  top  = 0;

        _selectionBorder = new Border
        {
            Width            = width  + 4,
            Height           = height + 4,
            BorderBrush      = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            BorderThickness  = new Thickness(1.5),
            Background       = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        InkCanvas.SetLeft(_selectionBorder, left - 2);
        InkCanvas.SetTop (_selectionBorder, top  - 2);
        DrawingCanvas.Children.Add(_selectionBorder);
    }

    private void UpdateSelectionBorderPosition()
    {
        if (_selectionBorder is null || _selectedElement is null) return;

        var left = InkCanvas.GetLeft(_selectedElement);
        var top  = InkCanvas.GetTop (_selectedElement);

        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top))  top  = 0;

        InkCanvas.SetLeft(_selectionBorder, left - 2);
        InkCanvas.SetTop (_selectionBorder, top  - 2);
    }

    private void ClearSelection()
    {
        if (_selectionBorder is not null)
        {
            DrawingCanvas.Children.Remove(_selectionBorder);
            _selectionBorder = null;
        }
        _selectedElement = null;
    }

    private void DeleteSelected()
    {
        if (_selectedElement is null) return;

        var el = _selectedElement;
        ClearSelection();
        DrawingCanvas.Children.Remove(el);
    }

    private TextBox BuildTextBox()
    {
        var tb = new TextBox
        {
            AcceptsReturn   = true,
            AcceptsTab      = false,
            TextWrapping    = TextWrapping.Wrap,

            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            BorderBrush     = new SolidColorBrush(_activeColor),
            Foreground      = new SolidColorBrush(_activeColor),
            CaretBrush      = new SolidColorBrush(_activeColor),
            FontSize        = Math.Max(14, _thickness * 5),

            MinWidth        = 80,
            MaxWidth        = DrawingCanvas.ActualWidth > 0
                                  ? DrawingCanvas.ActualWidth * 0.8
                                  : 400,

            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,

            IsHitTestVisible = true,
        };

        tb.TextChanged += (_, _) => tb.Height = double.NaN;

        AttachSelectHandlers(tb);

        return tb;
    }

    private void ActivateTextBox(TextBox tb)
    {
        if (_activeTextBox is not null && _activeTextBox != tb)
            CommitActiveTextBox();

        _activeTextBox = tb;

        tb.BorderThickness = new Thickness(0, 0, 0, 1);

        tb.PreviewKeyDown -= TextBox_PreviewKeyDown;
        tb.PreviewKeyDown += TextBox_PreviewKeyDown;

        tb.LostFocus -= TextBox_LostFocus;
        tb.LostFocus += TextBox_LostFocus;

        tb.Focus();
        Keyboard.Focus(tb);
        tb.CaretIndex = tb.Text.Length;
    }

    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CommitActiveTextBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelActiveTextBox();
            e.Handled = true;
        }
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && _activeTextBox == tb)
            CommitActiveTextBox();
    }

    private void CommitActiveTextBox()
    {
        if (_activeTextBox is null) return;

        var tb = _activeTextBox;
        _activeTextBox = null;

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

        if (string.IsNullOrWhiteSpace(tb.Text))
            DrawingCanvas.Children.Remove(tb);
        else
            tb.BorderThickness = new Thickness(0);

        Keyboard.Focus(this);
    }

    private static bool IsShapeVisible(Shape shape)
        => shape.Width > 2 || shape.Height > 2 ||
           (shape is Polyline pl && pl.Points.Count >= 2);

    private Shape? CreateShape(Tool tool, Point from, Point to) => tool switch
    {
        Tool.Rect => new Rectangle
        {
            Stroke          = new SolidColorBrush(_activeColor),
            StrokeThickness = _thickness,
            Fill            = Brushes.Transparent,
        },
        Tool.Highlight => new Rectangle
        {
            Fill   = new SolidColorBrush(Color.FromArgb(80, _activeColor.R, _activeColor.G, _activeColor.B)),
            Stroke = Brushes.Transparent,
        },
        Tool.Arrow => BuildArrowShape(from, to),
        _          => null
    };

    private static void UpdateShape(Shape shape, Tool tool, Point from, Point to)
    {
        switch (tool)
        {
            case Tool.Rect:
            case Tool.Highlight:
                InkCanvas.SetLeft(shape, Math.Min(from.X, to.X));
                InkCanvas.SetTop (shape, Math.Min(from.Y, to.Y));
                shape.Width  = Math.Abs(to.X - from.X);
                shape.Height = Math.Abs(to.Y - from.Y);
                break;

            case Tool.Arrow:
                if (shape is Polyline pl)
                    RebuildArrow(pl, from, to);
                break;
        }
    }

    private Polyline BuildArrowShape(Point from, Point to)
    {
        var pl = new Polyline
        {
            Stroke             = new SolidColorBrush(_activeColor),
            StrokeThickness    = _thickness,
            StrokeEndLineCap   = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeLineJoin     = PenLineJoin.Round,
        };
        RebuildArrow(pl, from, to);
        return pl;
    }

    private static void RebuildArrow(Polyline pl, Point from, Point to)
    {
        var dir = to - from;
        var len = dir.Length;
        if (len < 1) return;

        dir.Normalize();

        var headLen   = Math.Min(16.0, len * 0.35);
        var headAngle = Math.PI / 6.0;

        var left  = new Vector( Math.Cos(headAngle)  * (-dir.X) - Math.Sin(headAngle)  * (-dir.Y),
                                Math.Sin(headAngle)  * (-dir.X) + Math.Cos(headAngle)  * (-dir.Y));
        var right = new Vector( Math.Cos(-headAngle) * (-dir.X) - Math.Sin(-headAngle) * (-dir.Y),
                                Math.Sin(-headAngle) * (-dir.X) + Math.Cos(-headAngle) * (-dir.Y));

        pl.Points = new PointCollection { from, to, to + left * headLen, to, to + right * headLen };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        if (_activeTextBox is not null)
            CommitActiveTextBox();

        ClearSelection();

        var grid = CanvasHost;
        grid.Measure(new Size(grid.ActualWidth, grid.ActualHeight));
        grid.Arrange(new Rect(0, 0, grid.ActualWidth, grid.ActualHeight));

        var rtb = new RenderTargetBitmap(
            (int)grid.ActualWidth,
            (int)grid.ActualHeight,
            96, 96,
            PixelFormats.Pbgra32);

        rtb.Render(grid);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var stream = File.Create(_outputPath);
        encoder.Save(stream);

        DialogResult = true;
        Close();
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
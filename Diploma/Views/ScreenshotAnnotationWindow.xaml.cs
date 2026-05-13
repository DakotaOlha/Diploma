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
    private enum Tool { Arrow, Rect, Pen, Text, Highlight }
    
    private readonly Stack<UndoEntry> _undoStack = new();
    
    private readonly record struct UndoEntry
    {
        public Stroke?    Stroke  { get; init; }
        public UIElement? Element { get; init; }

        public static UndoEntry ForStroke (Stroke    s) => new() { Stroke  = s };
        public static UndoEntry ForElement(UIElement e) => new() { Element = e };
    }
    
    private Tool   _activeTool  = Tool.Arrow;
    private Color  _activeColor = Colors.Red;
    private double _thickness   = 2.0;

    private Point  _dragStart;
    private bool   _isDragging;
    private Shape? _previewShape; 

    private readonly string _outputPath;

    private static readonly Color[] Palette =
    [
        Colors.Red, Color.FromRgb(0xFF,0xA0,0x00),
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
            Color     = _activeColor,
            Width     = _thickness,
            Height    = _thickness,
            FitToCurve = true,
        };
        
        DrawingCanvas.Strokes.StrokesChanged += OnStrokesChanged;
    }
    
    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        foreach (var stroke in e.Added)
            _undoStack.Push(UndoEntry.ForStroke(stroke));
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        _activeTool = btn.Tag switch
        {
            "Arrow"     => Tool.Arrow,
            "Rect"      => Tool.Rect,
            "Pen"       => Tool.Pen,
            "Text"      => Tool.Text,
            "Highlight" => Tool.Highlight,
            _           => Tool.Arrow
        };

        foreach (var b in new[] { BtnArrow, BtnRect, BtnPen, BtnText, BtnHighlight })
            b.Style = (Style)Resources["ToolBtn"];

        btn.Style = (Style)Resources["ToolBtnActive"];

        DrawingCanvas.EditingMode = _activeTool == Tool.Pen
            ? InkCanvasEditingMode.Ink
            : InkCanvasEditingMode.None;
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
        if (DrawingCanvas is not null)
        {
            DrawingCanvas.DefaultDrawingAttributes.Width  = _thickness;
            DrawingCanvas.DefaultDrawingAttributes.Height = _thickness;
        }
    }

    private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool == Tool.Pen) return; 

        _dragStart  = e.GetPosition(DrawingCanvas);
        _isDragging = true;
        DrawingCanvas.CaptureMouse();

        if (_activeTool == Tool.Text)
        {
            PlaceTextBox(_dragStart);
            _isDragging = false;
            DrawingCanvas.ReleaseMouseCapture();
            return;
        }

        _previewShape = CreateShape(_activeTool, _dragStart, _dragStart);
        if (_previewShape is not null)
            DrawingCanvas.Children.Add(_previewShape);
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _previewShape is null)
            return;

        UpdateShape(_previewShape, _activeTool, _dragStart, e.GetPosition(DrawingCanvas));
    }

    private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) 
            return;
        
        _isDragging = false;
        DrawingCanvas.ReleaseMouseCapture();

        if (_previewShape is not null && IsShapeVisible(_previewShape))
            _undoStack.Push(UndoEntry.ForElement(_previewShape));

        _previewShape = null;
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
            Fill   = new SolidColorBrush(
                Color.FromArgb(80, _activeColor.R, _activeColor.G, _activeColor.B)),
            Stroke = Brushes.Transparent,
        },
        Tool.Arrow => BuildArrowShape(from, to),
        _          => null
    };

    private static void UpdateShape(Shape shape, Tool tool, Point from, Point to)
    {
        var x = Math.Min(from.X, to.X);
        var y = Math.Min(from.Y, to.Y);

        switch (tool)
        {
            case Tool.Rect:
            case Tool.Highlight:
                InkCanvas.SetLeft(shape, x);
                InkCanvas.SetTop(shape, y);
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
        var dir    = to - from;
        var len    = dir.Length;
        if (len < 1) 
            return;

        dir.Normalize();

        var headLen   = Math.Min(16.0, len * 0.35);
        var headAngle = Math.PI / 6.0;

        var left  = new Vector( Math.Cos(headAngle) * (-dir.X) - Math.Sin(headAngle) * (-dir.Y),
                                Math.Sin(headAngle) * (-dir.X) + Math.Cos(headAngle) * (-dir.Y));
        var right = new Vector( Math.Cos(-headAngle) * (-dir.X) - Math.Sin(-headAngle) * (-dir.Y),
                                Math.Sin(-headAngle) * (-dir.X) + Math.Cos(-headAngle) * (-dir.Y));

        pl.Points = new PointCollection { from, to, to + left * headLen, to, to + right * headLen };
    }

    private void PlaceTextBox(Point pos)
    {
        var tb = new TextBox
        {
            Background       = Brushes.Transparent,
            BorderThickness  = new Thickness(0, 0, 0, 1),
            BorderBrush      = new SolidColorBrush(_activeColor),
            Foreground       = new SolidColorBrush(_activeColor),
            FontSize         = Math.Max(14, _thickness * 5),
            MinWidth         = 80,
            CaretBrush       = new SolidColorBrush(_activeColor),
        };

        InkCanvas.SetLeft(tb, pos.X);
        InkCanvas.SetTop(tb, pos.Y);
        DrawingCanvas.Children.Add(tb);

        tb.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(tb.Text))
                DrawingCanvas.Children.Remove(tb);
            else
                _undoStack.Push(UndoEntry.ForElement(tb));
        };

        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                DrawingCanvas.Children.Remove(tb);
        };

        tb.Focus();
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
    
    private void Save_Click(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) 
            SaveAndClose();
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) 
            UndoLast();
        else if (e.Key == Key.Escape) 
            Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
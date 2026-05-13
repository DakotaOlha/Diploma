using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Diploma.Helpers;
using Diploma.Views.Controls;

namespace Diploma.Views
{
    public partial class ScreenshotAnnotationWindow : Window
    {
        private readonly AnnotationToolManager _tm = new();
        private readonly string _outputPath;
        private readonly Stack<UndoEntry> _undoStack = new();

        private UIElement? _selectedElement;
        private ResizeAdorner? _activeAdorner;
        private TextBox? _activeTextBox;

        private Point _dragStart;
        private bool _isDragging;
        private Shape? _previewShape;

        private readonly record struct UndoEntry
        {
            public Stroke? Stroke { get; init; }
            public UIElement? Element { get; init; }
        }

        public ScreenshotAnnotationWindow(BitmapSource screenshot, string outputPath)
        {
            InitializeComponent();
            _outputPath = outputPath;

            SourceImage.Source = screenshot;
            
            Width = Math.Min(screenshot.PixelWidth + 2, SystemParameters.PrimaryScreenWidth - 80);
            Height = Math.Min(screenshot.PixelHeight + 84, SystemParameters.PrimaryScreenHeight - 80);

            ColorPicker.ItemsSource = new[] 
            { 
                Colors.Red, Colors.Orange, Colors.Yellow, 
                Colors.LimeGreen, Colors.DeepSkyBlue, Colors.White, Colors.Black 
            };

            DrawingCanvas.Strokes.StrokesChanged += OnStrokesChanged;
            
            this.Loaded += (s, e) => this.Focus();
        }

        private void SelectElement(UIElement? element)
        {
            if (_selectedElement != null && _activeAdorner != null)
            {
                var layer = AdornerLayer.GetAdornerLayer(_selectedElement);
                layer?.Remove(_activeAdorner);
                _activeAdorner = null;
            }

            if (element == SourceImage || element == DrawingCanvas) element = null;

            _selectedElement = element;

            if (_selectedElement is FrameworkElement fe)
            {
                var layer = AdornerLayer.GetAdornerLayer(fe);
                if (layer != null)
                {
                    _activeAdorner = new ResizeAdorner(fe);
                    layer.Add(_activeAdorner);
                }
            }
        }


        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                    CommitActiveTextBox();
                else if (e.Key == Key.Escape)
                    CancelActiveTextBox();
                return; 
            }

            switch (e.Key)
            {
                case Key.V: SelectTool(AnnotationTool.Arrow); break;
                case Key.P: SelectTool(AnnotationTool.Pen); break;
                case Key.M: SelectTool(AnnotationTool.Highlight); break;
                case Key.T: SelectTool(AnnotationTool.Text); break;
                case Key.R: SelectTool(AnnotationTool.Rect); break;
                case Key.E: SelectTool(AnnotationTool.Eraser); break;
                case Key.Z when Keyboard.Modifiers == ModifierKeys.Control: UndoLast(); break;
                case Key.S when Keyboard.Modifiers == ModifierKeys.Control: SaveAndClose(); break;
                case Key.Delete: DeleteSelected(); break;
                case Key.Escape: SelectElement(null); break;
            }
        }

        private void DeleteSelected()
        {
            if (_selectedElement != null)
            {
                DrawingCanvas.Children.Remove(_selectedElement);
                SelectElement(null);
            }
        }

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                if (Enum.TryParse(tag, out AnnotationTool tool))
                    SelectTool(tool);
            }
        }

        private void SelectTool(AnnotationTool tool)
        {
            _tm.ActiveTool = tool;
            SelectElement(null);

            UpdateToolbarVisuals(tool);

            DrawingCanvas.EditingMode = tool switch
            {
                AnnotationTool.Pen => InkCanvasEditingMode.Ink,
                AnnotationTool.Eraser => InkCanvasEditingMode.EraseByPoint,
                _ => InkCanvasEditingMode.None
            };
        }

        private void UpdateToolbarVisuals(AnnotationTool tool)
        {
            var buttons = new[] { BtnArrow, BtnRect, BtnPen, BtnText, BtnHighlight, BtnEraser };
            foreach (var b in buttons) b.Style = (Style)Resources["ToolBtn"];

            var activeBtn = tool switch
            {
                AnnotationTool.Arrow => BtnArrow,
                AnnotationTool.Rect => BtnRect,
                AnnotationTool.Pen => BtnPen,
                AnnotationTool.Text => BtnText,
                AnnotationTool.Highlight => BtnHighlight,
                AnnotationTool.Eraser => BtnEraser,
                _ => null
            };
            if (activeBtn != null) activeBtn.Style = (Style)Resources["ToolBtnActive"];
        }

        private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_tm.ActiveTool == AnnotationTool.Pen || _tm.ActiveTool == AnnotationTool.Eraser) return;

            _dragStart = e.GetPosition(DrawingCanvas);

            if (_tm.ActiveTool == AnnotationTool.Arrow)
            {
                var hit = DrawingCanvas.InputHitTest(_dragStart) as UIElement;
                SelectElement(hit);
                return;
            }

            _isDragging = true;
            DrawingCanvas.CaptureMouse();

            if (_tm.ActiveTool == AnnotationTool.Text)
            {
                PlaceTextBox(_dragStart);
                _isDragging = false;
                DrawingCanvas.ReleaseMouseCapture();
            }
            else
            {
                _previewShape = CreateShape(_tm.ActiveTool, _dragStart, _dragStart);
                if (_previewShape != null) DrawingCanvas.Children.Add(_previewShape);
            }
        }

        private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _previewShape == null) return;
            UpdateShape(_previewShape, _tm.ActiveTool, _dragStart, e.GetPosition(DrawingCanvas));
        }

        private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            DrawingCanvas.ReleaseMouseCapture();

            if (_previewShape != null)
            {
                _undoStack.Push(new UndoEntry { Element = _previewShape });
                _previewShape = null;
            }
        }

        private void PlaceTextBox(Point pos)
        {
            var tb = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 80,
                MaxWidth = DrawingCanvas.ActualWidth - pos.X - 10,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush(_tm.ActiveColor),
                Foreground = new SolidColorBrush(_tm.ActiveColor),
                FontSize = 16 + _tm.Thickness,
                CaretBrush = new SolidColorBrush(_tm.ActiveColor),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            InkCanvas.SetLeft(tb, pos.X);
            InkCanvas.SetTop(tb, pos.Y);
            
            tb.TextChanged += (s, e) => tb.Height = double.NaN;
            tb.LostFocus += (s, e) => CommitActiveTextBox();

            DrawingCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.Focus();
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox == null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;

            if (string.IsNullOrWhiteSpace(tb.Text))
                DrawingCanvas.Children.Remove(tb);
            else
                _undoStack.Push(new UndoEntry { Element = tb });

            this.Focus();
        }

        private void CancelActiveTextBox()
        {
            if (_activeTextBox == null) return;
            DrawingCanvas.Children.Remove(_activeTextBox);
            _activeTextBox = null;
            this.Focus();
        }

        private Shape? CreateShape(AnnotationTool tool, Point from, Point to)
        {
            Shape? shape = tool switch
            {
                AnnotationTool.Rect => new Rectangle { Stroke = new SolidColorBrush(_tm.ActiveColor), Fill = Brushes.Transparent },
                AnnotationTool.Highlight => new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(80, _tm.ActiveColor.R, _tm.ActiveColor.G, _tm.ActiveColor.B)) },
                _ => null
            };

            if (shape != null)
            {
                shape.StrokeThickness = _tm.Thickness;
                UpdateShape(shape, tool, from, to);
            }
            return shape;
        }

        private void UpdateShape(Shape shape, AnnotationTool tool, Point from, Point to)
        {
            var x = Math.Min(from.X, to.X);
            var y = Math.Min(from.Y, to.Y);
            var w = Math.Abs(from.X - to.X);
            var h = Math.Abs(from.Y - to.Y);

            InkCanvas.SetLeft(shape, x);
            InkCanvas.SetTop(shape, y);
            shape.Width = w;
            shape.Height = h;
        }

        private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
        {
            foreach (var s in e.Added) _undoStack.Push(new UndoEntry { Stroke = s });
        }

        private void UndoLast()
        {
            if (!_undoStack.Any()) return;
            var entry = _undoStack.Pop();

            if (entry.Stroke != null) DrawingCanvas.Strokes.Remove(entry.Stroke);
            if (entry.Element != null) DrawingCanvas.Children.Remove(entry.Element);
        }

        private void SaveAndClose()
        {
            DialogResult = true;
            Close();
        }

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Color c })
            {
                _tm.ActiveColor = c;
                DrawingCanvas.DefaultDrawingAttributes.Color = c;
            }
        }
        
        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            UndoLast();
        }
        
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveAndClose();
        }
        
        private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_tm != null)
            {
                _tm.Thickness = e.NewValue;
                DrawingCanvas.DefaultDrawingAttributes.Width = e.NewValue;
                DrawingCanvas.DefaultDrawingAttributes.Height = e.NewValue;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    }
    
    public class ColorToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is Color color)
                return new SolidColorBrush(color);
            return Brushes.Red;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
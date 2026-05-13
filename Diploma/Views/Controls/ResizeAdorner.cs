using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Diploma.Views.Controls;

public class ResizeAdorner: Adorner
{
    private const double HandleSize = 8.0;
    private readonly VisualCollection _visualChildren;
    private readonly Thumb[] _thumbs = new Thumb[8];

    public ResizeAdorner(UIElement adornedElement) : base(adornedElement)
    {
        _visualChildren = new VisualCollection(this);

        for (int i = 0; i < 8; i++)
        {
            var thumb = new Thumb
            {
                Width = HandleSize, Height = HandleSize,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = GetCursorForHandle(i)
            };
            
            int index = i;
            thumb.DragDelta += (s, e) => OnHandleDrag(index, e.HorizontalChange, e.VerticalChange);
            
            _thumbs[i] = thumb;
            _visualChildren.Add(thumb);
        }
    }

    private void OnHandleDrag(int index, double dx, double dy)
    {
        var element = (FrameworkElement)AdornedElement;
        double left = InkCanvas.GetLeft(element);
        double top = InkCanvas.GetTop(element);

        switch (index)
        {
            case 0:
                element.Width = Math.Max(20, element.Width - dx);
                element.Height = Math.Max(20, element.Height - dy);
                InkCanvas.SetLeft(element, left + dx);
                InkCanvas.SetTop(element, top + dy);
                break;
            case 2:
                element.Width = Math.Max(20, element.Width + dx);
                element.Height = Math.Max(20, element.Height - dy);
                InkCanvas.SetTop(element, top + dy);
                break;
            case 5:
                element.Width = Math.Max(20, element.Width - dx);
                element.Height = Math.Max(20, element.Height + dy);
                InkCanvas.SetLeft(element, left + dx);
                break;
            case 7:
                element.Width = Math.Max(20, element.Width + dx);
                element.Height = Math.Max(20, element.Height + dy);
                break;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double w = AdornedElement.RenderSize.Width;
        double h = AdornedElement.RenderSize.Height;

        Point[] points = {
            new(0, 0), new(w/2, 0), new(w, 0),
            new(0, h/2),            new(w, h/2),
            new(0, h), new(w/2, h), new(w, h)
        };

        for (int i = 0; i < 8; i++)
            _thumbs[i].Arrange(new Rect(points[i].X - HandleSize/2, points[i].Y - HandleSize/2, HandleSize, HandleSize));

        return finalSize;
    }

    private Cursor GetCursorForHandle(int index) => index switch {
        0 or 7 => Cursors.SizeNWSE,
        2 or 5 => Cursors.SizeNESW,
        1 or 6 => Cursors.SizeNS,
        _ => Cursors.SizeWE
    };

    protected override int VisualChildrenCount => _visualChildren.Count;
    protected override Visual GetVisualChild(int index) => _visualChildren[index];
}
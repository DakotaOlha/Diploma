using System.Windows.Media;

namespace Diploma.Helpers;

public enum AnnotationTool { Arrow, Rect, Pen, Text, Highlight, Eraser }

public class AnnotationToolManager
{
    public AnnotationTool ActiveTool { get; set; } = AnnotationTool.Arrow;
    public Color ActiveColor { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 2.0;
}
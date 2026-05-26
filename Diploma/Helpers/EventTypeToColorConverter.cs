using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Diploma.Helpers;

public sealed class EventTypeToColorConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Clipboard
        ["CLIPBOARD_COPY"]  = Brush("#4FC3F7"), // light blue
        ["CLIPBOARD_PASTE"] = Brush("#0288D1"), // blue

        // File operations
        ["FILE_SAVE"]       = Brush("#81C784"), // green
        ["FILE_SAVED_AUTO"] = Brush("#388E3C"), // dark green
        ["FILE_SWITCHED"]   = Brush("#A5D6A7"), // pale green

        // Execution
        ["RUN_OR_DEBUG"]    = Brush("#FF8A65"), // orange

        // Edit
        ["UNDO"]            = Brush("#CE93D8"), // purple

        // IDE lifecycle
        ["IDE_OPENED"]      = Brush("#80CBC4"), // teal
        ["IDE_CLOSED"]      = Brush("#FF8A80"), // red

        // Idle
        ["IDLE_START"]      = Brush("#BDBDBD"), // grey
        ["IDLE_END"]        = Brush("#E0E0E0"), // light grey

        // Markers & screenshots
        ["MANUAL_MARKER"]   = Brush("#FFD54F"), // amber  ← most important
        ["SCREENSHOT"]      = Brush("#F48FB1"), // pink

        // Rule violations — bright red, draws immediate attention
        ["RULE_VIOLATION"]  = Brush("#FF1744"),
    };

    private static readonly SolidColorBrush Fallback = Brush("#90A4AE");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string eventType && Map.TryGetValue(eventType, out var brush) ? brush : Fallback;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
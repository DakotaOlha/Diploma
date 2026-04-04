using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Diploma.Helpers;

public static class CapturePickerHelper
{
    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    public static async Task<GraphicsCaptureItem?> PickAsync(IntPtr hwnd)
    {
        var picker = new GraphicsCapturePicker();
        ((IInitializeWithWindow)(object)picker).Initialize(hwnd);
        return await picker.PickSingleItemAsync();
    }
}
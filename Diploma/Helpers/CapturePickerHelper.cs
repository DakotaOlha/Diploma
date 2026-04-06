using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace Diploma.Helpers;

public static class CapturePickerHelper
{
    public static async Task<GraphicsCaptureItem?> PickAsync(IntPtr hwnd)
    {
        var picker = new GraphicsCapturePicker();

        // WinRT.Interop.InitializeWithWindow — правильний спосіб
        InitializeWithWindow.Initialize(picker, hwnd);

        return await picker.PickSingleItemAsync();
    }
}
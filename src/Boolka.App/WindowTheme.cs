using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Boolka;

internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Attach(Window window) => window.SourceInitialized += (_, _) => Refresh(window);

    public static void Refresh(Window window)
    {
        // Не создаём HWND ради оформления: скрытые проверки остаются без нативных окон.
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // Квадратные углы и отсутствие системной однопиксельной рамки согласованы с оформлением WPF.
        var squareCorners = 1;
        DwmSetWindowAttribute(handle, 33, ref squareCorners, sizeof(int));
        var noBorder = -2;
        DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        var dark = ThemePalette.IsLight ? 0 : 1;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        SetColor(handle, 35, "#10131B");
        SetColor(handle, 36, "#E9EDF5");
        // Старые Windows могут не поддерживать цвета заголовка; нативное поведение сохраняется.
    }

    private static void SetColor(IntPtr handle, int attribute, string source)
    {
        var color = (Color)ColorConverter.ConvertFromString(ThemePalette.Map(source));
        var colorRef = color.R | color.G << 8 | color.B << 16;
        DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Boolka;

internal static class WindowWorkArea
{
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source) source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => WindowMessage(window, hwnd, message, wParam, lParam, ref handled));
        };
    }

    // Win32 передаёт физические пиксели: пересчёт через DPI здесь дал бы неверный размер.
    internal static Rect MaximizedBounds(Rect monitor, Rect work) =>
        new(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);

    private static IntPtr WindowMessage(Window window, IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return IntPtr.Zero;
        var bounds = MaximizedBounds(info.Monitor.ToRect(), info.Work.ToRect());
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new() { X = (int)bounds.X, Y = (int)bounds.Y };
        limits.MaxSize = new() { X = (int)bounds.Width, Y = (int)bounds.Height };
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX));
        limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY));
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

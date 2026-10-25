using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VOIDOverlay.Tacho;

public static class PositionService
{
    private const int MonitorDpiType = 0;

    public static void Apply(Window window, AppSettings settings)
    {
        var monitors = Forms.Screen.AllScreens;
        if (monitors.Length == 0) return;

        var screen = monitors.FirstOrDefault(m =>
            m.DeviceName.Equals(settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen
            ?? monitors[0];
        var windowWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var windowHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        var (scaleX, scaleY) = GetScale(screen);
        var bounds = settings.EdgeReference == OverlayEdgeReference.FullScreen ? screen.Bounds : screen.WorkingArea;
        var area = new Rect(bounds.Left / scaleX, bounds.Top / scaleY, bounds.Width / scaleX, bounds.Height / scaleY);
        var placement = ComputePlacement(area, windowWidth, windowHeight, settings.Position, settings.OffsetX, settings.OffsetY);
        window.Left = placement.X;
        window.Top = placement.Y;
    }

    public static (double X, double Y) ComputePlacement(
        Rect workArea, double windowWidth, double windowHeight, OverlayPosition position, int offsetX, int offsetY)
    {
        const double margin = 10;
        var x = position switch
        {
            OverlayPosition.TopLeft or OverlayPosition.BottomLeft => workArea.Left + margin,
            OverlayPosition.TopCenter or OverlayPosition.BottomCenter => workArea.Left + (workArea.Width - windowWidth) / 2,
            _ => workArea.Right - windowWidth - margin
        } + offsetX;
        var y = position switch
        {
            OverlayPosition.TopLeft or OverlayPosition.TopCenter or OverlayPosition.TopRight => workArea.Top + margin,
            _ => workArea.Bottom - windowHeight - margin
        } + offsetY;
        return (
            Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - windowWidth)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - windowHeight)));
    }

    private static (double X, double Y) GetScale(Forms.Screen screen)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint(screen.WorkingArea.Left + 1, screen.WorkingArea.Top + 1), 2);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MonitorDpiType, out var dpiX, out var dpiY) == 0)
            {
                return (dpiX / 96d, dpiY / 96d);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return (1, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}

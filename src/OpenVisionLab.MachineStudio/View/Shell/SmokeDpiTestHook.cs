using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace OpenVisionLab.MachineStudio.View.Shell;

/// <summary>
/// Applies the same per-monitor DPI message that Windows sends when a window
/// crosses monitors. This is used only by the command-line smoke harness and
/// never changes the user's display settings.
/// </summary>
internal static class SmokeDpiTestHook
{
    private const uint WmDpiChanged = 0x02E0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const double DefaultDpi = 96.0;

    public static void PlaceOnTestMonitor(Window window, int logicalWidth, int logicalHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        var monitor = SelectTestMonitor();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = monitor.Bounds.Left;
        window.Top = monitor.Bounds.Top;
        window.Width = logicalWidth;
        window.Height = logicalHeight;
    }

    public static void Apply(Window window, int scalePercent, int logicalWidth, int logicalHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (scalePercent is < 100 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scalePercent),
                scalePercent,
                "The smoke DPI scale must be between 100 and 200 percent.");
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The smoke DPI hook requires a shown window.");
        }

        var dpi = checked((uint)Math.Round(DefaultDpi * scalePercent / 100.0));
        var scale = dpi / DefaultDpi;
        var monitor = SelectTestMonitor();
        var suggestedBounds = new NativeRect
        {
            Left = monitor.Bounds.Left,
            Top = monitor.Bounds.Top,
            Right = checked(monitor.Bounds.Left + (int)Math.Round(logicalWidth * scale)),
            Bottom = checked(monitor.Bounds.Top + (int)Math.Round(logicalHeight * scale))
        };
        var packedDpi = new IntPtr(unchecked((int)(dpi | (dpi << 16))));
        SendMessage(handle, WmDpiChanged, packedDpi, ref suggestedBounds);
        if (!SetWindowPos(
                handle,
                IntPtr.Zero,
                suggestedBounds.Left,
                suggestedBounds.Top,
                suggestedBounds.Right - suggestedBounds.Left,
                suggestedBounds.Bottom - suggestedBounds.Top,
                SwpNoZOrder | SwpNoActivate))
        {
            throw new InvalidOperationException("The smoke window could not be placed on the test monitor.");
        }
        window.UpdateLayout();

        var observed = VisualTreeHelper.GetDpi(window);
        if (Math.Abs(observed.PixelsPerInchX - dpi) > 0.5 ||
            Math.Abs(observed.PixelsPerInchY - dpi) > 0.5)
        {
            throw new InvalidOperationException(
                $"WPF did not apply the requested smoke DPI. Requested {dpi}, " +
                $"observed {observed.PixelsPerInchX:F1} x {observed.PixelsPerInchY:F1}.");
        }
    }

    public static SmokeMonitorEvidence CaptureMonitorEvidence(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
        {
            throw new InvalidOperationException("The smoke monitor evidence requires a shown window.");
        }

        var monitor = Forms.Screen.FromHandle(handle);
        var windowBounds = System.Drawing.Rectangle.FromLTRB(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right,
            windowRect.Bottom);
        var intersects = monitor.Bounds.IntersectsWith(windowBounds);
        var contained = monitor.Bounds.Contains(windowBounds);

        return new SmokeMonitorEvidence(
            monitor.DeviceName,
            monitor.Primary,
            FormatBounds(monitor.Bounds),
            FormatBounds(monitor.WorkingArea),
            $"{windowRect.Left},{windowRect.Top}," +
            $"{windowRect.Right - windowRect.Left},{windowRect.Bottom - windowRect.Top}",
            intersects,
            contained);
    }

    private static Forms.Screen SelectTestMonitor()
    {
        var monitors = Forms.Screen.AllScreens;
        if (monitors.Length == 1)
        {
            return monitors[0];
        }

        return monitors
            .OrderBy(monitor => monitor.Bounds.Left)
            .ThenBy(monitor => monitor.WorkingArea.Width * monitor.WorkingArea.Height)
            .First();
    }

    private static string FormatBounds(System.Drawing.Rectangle bounds) =>
        $"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}";

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        ref NativeRect lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed record SmokeMonitorEvidence(
    string DeviceName,
    bool IsPrimary,
    string Bounds,
    string WorkingArea,
    string WindowRect,
    bool WindowIntersectsMonitor,
    bool WindowContainedByMonitor);

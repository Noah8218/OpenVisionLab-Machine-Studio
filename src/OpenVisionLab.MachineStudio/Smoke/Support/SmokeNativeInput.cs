using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OpenVisionLab.MachineStudio;

/// <summary>
/// Owns Direct EXE smoke user32 input and the lifetime of a held pointer.
/// </summary>
internal sealed class SmokeNativeInput : IDisposable
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint MapVirtualKeyToScanCode = 0;
    private const uint GetAncestorRoot = 2;

    private bool _pointerHeld;
    private bool _disposed;

    internal bool IsPointerHeld => _pointerHeld;

    internal void ActivateWindow(Window window) => SetForegroundWindow(new WindowInteropHelper(window).Handle);

    internal void SendMouseEvent(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo) => mouse_event(flags, dx, dy, data, extraInfo);

    internal void PressLeftButton() => SendMouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);

    internal void SetCursorPosition(int x, int y) => SetCursorPos(x, y);

    internal (int X, int Y) GetCursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    internal void MovePointerToCenter(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(
            Math.Max(1, element.ActualWidth / 2),
            Math.Max(1, element.ActualHeight / 2)));
        var targetX = (int)Math.Round(point.X);
        var targetY = (int)Math.Round(point.Y);
        var current = GetCursorPosition();
        if (current.X == targetX && current.Y == targetY)
        {
            SetCursorPosition(targetX + 1, targetY);
        }
        SetCursorPosition(targetX, targetY);
        Mouse.Synchronize();
    }

    internal void MarkPointerHeld() => _pointerHeld = true;

    internal (bool IsOwned, string Diagnostic) CheckPointerOwnership(Window window)
    {
        if (!GetCursorPos(out var cursorPosition))
        {
            return (false, "GetCursorPos failed.");
        }

        var targetWindow = new WindowInteropHelper(window).Handle;
        var pointerWindow = GetAncestor(WindowFromPoint(cursorPosition), GetAncestorRoot);
        var foregroundWindow = GetAncestor(GetForegroundWindow(), GetAncestorRoot);
        var isOwned = targetWindow != IntPtr.Zero
            && pointerWindow == targetWindow
            && foregroundWindow == targetWindow;
        var diagnostic =
            $"Target=0x{targetWindow.ToInt64():X}, " +
            $"PointerRoot=0x{pointerWindow.ToInt64():X}, " +
            $"ForegroundRoot=0x{foregroundWindow.ToInt64():X}, " +
            $"Cursor=({cursorPosition.X},{cursorPosition.Y}).";
        return (isOwned, diagnostic);
    }

    internal void SendKey(byte virtualKey)
    {
        var scanCode = (byte)MapVirtualKey(virtualKey, MapVirtualKeyToScanCode);
        var extended = virtualKey is >= 0x21 and <= 0x28 ? KeyEventExtended : 0;
        keybd_event(virtualKey, scanCode, extended, UIntPtr.Zero);
        keybd_event(virtualKey, scanCode, extended | KeyEventKeyUp, UIntPtr.Zero);
    }

    internal void ReleasePointer()
    {
        if (!_pointerHeld)
        {
            return;
        }

        SendMouseEvent(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        Mouse.Capture(null);
        Mouse.Synchronize();
        _pointerHeld = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleasePointer();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint dwFlags,
        uint dx,
        uint dy,
        uint dwData,
        UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}

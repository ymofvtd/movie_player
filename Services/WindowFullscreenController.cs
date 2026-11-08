using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Person_Movie_Player.Services;

/// <summary>Changes chrome and bounds without replacing or reparenting the video host.</summary>
public sealed class WindowFullscreenController(Window window)
{
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResizeMode;
    private bool _active;

    public void SetFullscreen(bool fullscreen)
    {
        if (_active == fullscreen) return;
        if (!fullscreen)
        {
            window.WindowState = WindowState.Normal;
            window.WindowStyle = _restoreStyle;
            window.ResizeMode = _restoreResizeMode;
            window.Left = _restoreBounds.Left;
            window.Top = _restoreBounds.Top;
            window.Width = _restoreBounds.Width;
            window.Height = _restoreBounds.Height;
            window.WindowState = _restoreState;
            _active = false;
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _restoreBounds = window.RestoreBounds;
        _restoreState = window.WindowState;
        _restoreStyle = window.WindowStyle;
        _restoreResizeMode = window.ResizeMode;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        _active = true;
        var bounds = monitor.Monitor;
        if (!SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top, 0x0024))
        {
            var error = Marshal.GetLastWin32Error();
            SetFullscreen(false);
            throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

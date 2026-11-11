using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Person_Movie_Player.Services;

/// <summary>Video and transparent controls share the main window's native child tree and DWM animation.</summary>
public sealed class NativeVideoHost(MediaPlayer player, FrameworkElement overlay) : HwndHost
{
    private IntPtr _videoHandle;
    private HwndSource? _overlaySource;
    private const int ChildStyles = 0x40000000 | 0x10000000 | 0x04000000; // CHILD, VISIBLE, CLIPSIBLINGS

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var container = CreateWindowEx(0, "static", "MyCinema video", ChildStyles | 0x02000000 | 4,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (container == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            _videoHandle = CreateWindowEx(0, "static", "Video", ChildStyles | 4,
                0, 0, 1, 1, container, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_videoHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _overlaySource = new HwndSource(new HwndSourceParameters("MyCinema playback controls", 1, 1)
            {
                ParentWindow = container, WindowStyle = ChildStyles,
                PositionX = 0, PositionY = 0, UsesPerPixelTransparency = true, TreatAsInputRoot = true
            });
            _overlaySource.CompositionTarget.BackgroundColor = Colors.Transparent;
            _overlaySource.RootVisual = overlay;
            player.Hwnd = _videoHandle;
            return new HandleRef(this, container);
        }
        catch
        {
            ReleaseOverlay();
            DestroyWindow(container);
            _videoHandle = IntPtr.Zero;
            throw;
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0005 && _overlaySource is not null) // WM_SIZE: native pixel bounds, including DPI changes.
        {
            var width = (int)(lParam.ToInt64() & 0xffff);
            var height = (int)((lParam.ToInt64() >> 16) & 0xffff);
            SetWindowPos(_videoHandle, IntPtr.Zero, 0, 0, width, height, 0x0014); // NOZORDER, NOACTIVATE
            SetWindowPos(_overlaySource.Handle, IntPtr.Zero, 0, 0, width, height, 0x0010);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void ReleaseOverlay()
    {
        if (_overlaySource is null) return;
        _overlaySource.RootVisual = null;
        _overlaySource.Dispose();
        _overlaySource = null;
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (player.NativeReference != IntPtr.Zero && player.Hwnd == _videoHandle) player.Hwnd = IntPtr.Zero;
        ReleaseOverlay();
        DestroyWindow(hwnd.Handle);
        _videoHandle = IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);
}

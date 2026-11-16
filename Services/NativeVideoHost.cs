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
    private const int ClipChildren = 0x02000000;
    private static readonly WindowProcedure BlackWindowProcedure = DefWindowProc;
    private static readonly Lazy<string> BlackWindowClass = new(RegisterBlackWindowClass);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var windowClass = BlackWindowClass.Value;
        var instance = GetModuleHandle(null);
        var container = CreateWindowEx(0, windowClass, "MyCinema video", ChildStyles | ClipChildren,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (container == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            _videoHandle = CreateWindowEx(0, windowClass, "Video", ChildStyles | ClipChildren,
                0, 0, 1, 1, container, IntPtr.Zero, instance, IntPtr.Zero);
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

    private static string RegisterBlackWindowClass()
    {
        const string name = "MyCinema.BlackVideoSurface";
        // STATIC/SS_BLACKRECT uses the system window-frame color, which can be gray.
        // A real black class brush also erases exposed letterbox areas after resizing.
        var windowClass = new NativeWindowClass
        {
            Style = 0x0001 | 0x0002, // CS_VREDRAW, CS_HREDRAW
            Procedure = Marshal.GetFunctionPointerForDelegate(BlackWindowProcedure),
            Instance = GetModuleHandle(null), Background = GetStockObject(4), // BLACK_BRUSH
            Name = name
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410) throw new Win32Exception(error); // ERROR_CLASS_ALREADY_EXISTS
        }
        return name;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string Name;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref NativeWindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectIndex);
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

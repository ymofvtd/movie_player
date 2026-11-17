using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Person_Movie_Player.Views;
using Xunit;

namespace MyCinema.Tests;

public sealed partial class LibraryTemplateTests
{
    // Called on the shared WPF STA thread with the real VLC output and transparent native child.
    private static void VerifyNativeVideoOverlay()
    {
        using var playback = new PlaybackService(NullLogger<PlaybackService>.Instance, ["--aout=dummy"]);
        playback.InitializeAsync().GetAwaiter().GetResult();
        var clock = new LayoutClock();
        using var player = new PlayerViewModel(playback, new PreviewDialogs(),
            new NavigationService(NullLogger<NavigationService>.Instance),
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher), timeProvider: clock);
        var view = new PlayerView { DataContext = player };
        var window = new Window { Content = view, Width = 1280, Height = 720, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = 0 };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            window.Show();
            PumpUntil(() => playback.MediaPlayer!.Hwnd != IntPtr.Zero);
            AssertPaintsBlack(playback.MediaPlayer!.Hwnd);
            AssertPaintsBlack(GetParent(playback.MediaPlayer.Hwnd));
            playback.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv")).GetAwaiter().GetResult();
            PumpUntil(() => player.IsPlaying);
            var native = playback.MediaPlayer!;
            var handle = native.Hwnd;
            var host = Assert.IsType<Grid>(view.FindName("VideoHost"));
            var video = Assert.Single(host.Children.OfType<NativeVideoHost>());
            var overlay = Assert.IsType<Grid>(view.FindName("PlayerOverlay"));
            var mainHandle = new WindowInteropHelper(window).Handle;
            var overlayHandle = Assert.IsType<HwndSource>(PresentationSource.FromVisual(overlay)).Handle;
            Assert.True(IsChild(mainHandle, overlayHandle));
            Assert.True(IsChild(mainHandle, handle));
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.Same(player, overlay.DataContext);
            PumpUntil(() => overlay.RenderSize == host.RenderSize);
            var size = host.RenderSize;
            var ratio = native.AspectRatio;
            playback.Pause();
            PumpUntil(() => playback.Current.State == PlaybackState.Paused && playback.Current.CanSeek);
            playback.Seek(5000);
            PumpUntil(() => Math.Abs(native.Time - 5000) < 200);
            var reloadButton = Assert.IsType<Button>(view.FindName("ReloadButton"));
            Assert.Same(player.ReloadCommand, reloadButton.Command);
            reloadButton.Command.Execute(null);
            PumpUntil(() => !player.ReloadCommand.IsRunning && playback.Current.State == PlaybackState.Paused &&
                playback.Current.CanSeek && Math.Abs(native.Time - 5000) < 200);
            Assert.Equal(handle, native.Hwnd);
            Assert.Equal(size, host.RenderSize);
            Assert.True(IsChild(mainHandle, overlayHandle));
            playback.PlayAsync().GetAwaiter().GetResult();
            PumpUntil(() => player.IsPlaying);
            var controls = Assert.IsType<Border>(view.FindName("PlaybackControls"));
            var playPause = Assert.Single(Descendants(controls).OfType<Button>(), button => button.Command == player.PlayPauseCommand);
            // Focus left on Play must not hold controls open, including in windowed playback.
            playPause.Focus();
            PumpUntil(() => playPause.IsKeyboardFocused);
            clock.Advance();
            PumpUntil(() => !player.AreControlsVisible);
            Assert.Equal(Visibility.Collapsed, controls.Visibility);
            Assert.Equal(Cursors.None, overlay.Cursor);
            Assert.True(overlay.IsKeyboardFocused);
            Assert.Equal(size, host.RenderSize);
            player.IsFullscreen = true;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                clock.Advance();
                player.UpdateControlsVisibility(false);
                window.UpdateLayout();
                Assert.False(player.AreControlsVisible);
                Assert.Equal(size, host.RenderSize);
                player.NotifyInteraction();
                window.UpdateLayout();
                Assert.Equal(size, host.RenderSize);
                Assert.Equal(size, video.RenderSize);
                Assert.Equal(handle, native.Hwnd);
                Assert.Equal(ratio, native.AspectRatio);
            }
            player.ToggleControlsLockCommand.Execute(null);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay), 0, Key.L)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(key);
            Assert.True(key.Handled);
            Assert.False(player.AreControlsLocked);
            foreach (var state in new[] { WindowState.Maximized, WindowState.Normal, WindowState.Minimized, WindowState.Normal })
            {
                window.WindowState = state;
                PumpUntil(() => window.WindowState == state);
                Assert.True(IsChild(mainHandle, overlayHandle));
                Assert.Equal(handle, native.Hwnd);
                if (state != WindowState.Minimized)
                {
                    PumpUntil(() => overlay.RenderSize == host.RenderSize);
                    AssertPaintsBlack(handle);
                    AssertPaintsBlack(GetParent(handle));
                }
            }
            // Unloading and reloading must release and reattach the native child cleanly.
            window.Content = null;
            PumpUntil(() => !view.IsLoaded);
            Assert.Contains(overlay, Assert.IsType<Grid>(view.FindName("PlayerLayout")).Children.Cast<UIElement>());
            window.Content = view;
            PumpUntil(() => view.IsLoaded && native.Hwnd != IntPtr.Zero);
            Assert.Same(player, overlay.DataContext);
        }
        finally
        {
            playback.StopAsync().GetAwaiter().GetResult();
            window.Close();
            PumpUntil(() => !view.IsLoaded);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    // Exercise the native background paint, which WPF RenderTargetBitmap cannot capture.
    // Letterbox areas must stay black even when Windows uses a gray window-frame color.
    private static void AssertPaintsBlack(IntPtr window)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 1, 1);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Assert.True(SetPixelV(memory, 0, 0, 0x00FF00FF));
            SendMessage(window, 0x0014, memory, IntPtr.Zero); // WM_ERASEBKGND
            Assert.Equal(0u, GetPixel(memory, 0, 0));
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPixelV(IntPtr deviceContext, int x, int y, uint color);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr deviceContext, int x, int y);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (condition()) return;
            Thread.Sleep(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(8));
        Assert.True(condition(), "The native WPF video overlay did not reach the expected state.");
    }
}

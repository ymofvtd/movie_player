using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.WPF;
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
    // Called on the shared WPF STA thread so this also exercises VLC's detached overlay.
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
        try
        {
            window.Show();
            PumpUntil(() => playback.MediaPlayer!.Hwnd != IntPtr.Zero);
            playback.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv")).GetAwaiter().GetResult();
            PumpUntil(() => player.IsPlaying);
            var native = playback.MediaPlayer!;
            var handle = native.Hwnd;
            var host = Assert.IsType<Grid>(view.FindName("VideoHost"));
            var video = Assert.Single(host.Children.OfType<VideoView>());
            var overlay = Assert.IsType<Grid>(view.FindName("PlayerOverlay"));
            Assert.NotSame(window, Window.GetWindow(overlay));
            Assert.Same(player, overlay.DataContext);
            var size = host.RenderSize;
            var ratio = native.AspectRatio;
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
            // Unloading and reloading must release and reattach the owned overlay cleanly.
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
        }
    }

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

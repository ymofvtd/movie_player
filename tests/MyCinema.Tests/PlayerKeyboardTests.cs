using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Playback;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed partial class LibraryTemplateTests
{
    // Runs on the shared WPF STA thread, including real keyboard event routing rules.
    private static void VerifyFrameKeyboardShortcuts()
    {
        var playback = new PreviewPlayback
        {
            Current = new(PlaybackState.Paused, "film.mkv", 5000, 60000, true) { VideoFrameDurationMs = 40 }
        };
        using var player = new PlayerViewModel(playback, new PreviewDialogs(),
            new NavigationService(NullLogger<NavigationService>.Instance), new PreviewDispatcher());
        var source = new TestPresentationSource();
        KeyEventArgs Press(Key key, object? focus = null, bool visible = true)
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = focus ?? new Grid() };
            PlayerKeyboard.Handle(input, player, visible);
            return input;
        }
        Assert.True(Press(Key.OemPeriod).Handled);
        Assert.Equal(5040, playback.LastSeek);
        Assert.True(Press(Key.OemComma, new Button()).Handled);
        Assert.Equal(4960, playback.LastSeek);
        foreach (var focus in new object[] { new TextBox(), new PasswordBox(), new ComboBox() })
        {
            Assert.False(Press(Key.OemPeriod, focus).Handled);
            Assert.Equal(4960, playback.LastSeek);
        }
        Assert.False(Press(Key.OemPeriod, visible: false).Handled);
        Assert.Equal(4960, playback.LastSeek);
        Assert.True(Press(Key.Right).Handled);
        Assert.Equal(10000, playback.LastSeek);
        playback.Emit(playback.Current with { State = PlaybackState.Playing, PositionMs = 20000, Revision = 1 });
        Assert.True(Press(Key.Left).Handled);
        Assert.Equal(15000, playback.LastSeek);
        Press(Key.OemPeriod);
        Assert.Equal(15000, playback.LastSeek);
        player.IsFullscreen = true;
        player.ToggleControlsLockCommand.Execute(null);
        Assert.True(Press(Key.OemComma).Handled);
        Assert.True(Press(Key.OemPeriod).Handled);
        Assert.True(Press(Key.Right).Handled);
        Assert.Equal(15000, playback.LastSeek);
    }
}

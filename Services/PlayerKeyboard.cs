using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player.Services;

public static class PlayerKeyboard
{
    public static void Handle(KeyEventArgs e, PlayerViewModel player, bool playerVisible)
    {
        if (playerVisible) player.NotifyInteraction();
        if (!e.Handled && e.Key == Key.Escape && player.IsFullscreen && !IsOpenComboBox(e.OriginalSource as DependencyObject))
        {
            player.ExitFullscreenCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Handled || IsTextInput(e.OriginalSource as DependencyObject)) return;
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (player.OpenFileCommand.CanExecute(null)) player.OpenFileCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!playerVisible || Keyboard.Modifiers != ModifierKeys.None) return;
        // Let focused controls retain Space/arrow/Enter behavior (including episode lists and sliders).
        if (e.Key is Key.Space or Key.Left or Key.Right or Key.Up or Key.Down &&
            OwnsNavigationKeys(e.OriginalSource as DependencyObject)) return;

        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Space => player.PlayPauseCommand,
            Key.Left => player.RewindCommand,
            Key.Right => player.ForwardCommand,
            Key.F => player.FullscreenCommand,
            Key.Escape when player.IsFullscreen => player.ExitFullscreenCommand,
            Key.M => player.MuteCommand,
            Key.N => player.NextEpisodeCommand,
            Key.P => player.PreviousEpisodeCommand,
            Key.S => player.SubtitleMenuCommand,
            _ => null
        };
        if (command is not null)
        {
            if (!e.IsRepeat && command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            player.AdjustVolume(e.Key == Key.Up ? 5 : -5);
            e.Handled = true;
        }
    }

    public static bool OwnsNavigationKeys(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase or PasswordBox or ComboBox or ButtonBase or Slider or ListBoxItem or TreeViewItem) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsOpenComboBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ComboBox { IsDropDownOpen: true }) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsTextInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase or PasswordBox or ComboBox) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
}

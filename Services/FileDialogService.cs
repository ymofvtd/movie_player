using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using MyCinema.Core.Playback;

namespace Person_Movie_Player.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? ChoosePosterFile()
    {
        var dialog = new OpenFileDialog { Title = "Choose a poster", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff",
            CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ChooseVideoFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a local video",
            Filter = "Video files|*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts|All files|*.*",
            CheckFileExists = true, Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ChooseSubtitleFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load local subtitles",
            Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx|All files|*.*",
            CheckFileExists = true, Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public ResumeChoice ChooseResume(string title, long positionMs)
    {
        var choice = ResumeChoice.Cancel;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"Resume from {PlaybackMath.FormatTime(positionMs)}?",
            Margin = new Thickness(0, 16, 0, 24), FontSize = 15 });
        var window = new Window
        {
            Title = "Resume playback", Owner = Application.Current.MainWindow, Width = 460,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceBrush")
        };
        var buttons = new WrapPanel();
        foreach (var (label, value) in new[] { ("Resume", ResumeChoice.Resume), ("Start Over", ResumeChoice.StartOver), ("Cancel", ResumeChoice.Cancel) })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0),
                IsDefault = value == ResumeChoice.Resume, IsCancel = value == ResumeChoice.Cancel,
                Style = (Style)Application.Current.FindResource(value == ResumeChoice.Resume ? "PrimaryButtonStyle" : "SecondaryButtonStyle") };
            button.Click += (_, _) => { choice = value; window.DialogResult = value != ResumeChoice.Cancel; };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        window.ShowDialog();
        return choice;
    }
}

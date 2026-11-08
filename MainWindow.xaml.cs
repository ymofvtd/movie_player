using System.Windows;
using Person_Movie_Player.ViewModels;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Person_Movie_Player.Services;
using System.Windows.Controls;
using System.Windows.Media;

namespace Person_Movie_Player;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ILogger<MainWindow> _logger;
    private readonly WindowFullscreenController _fullscreen;
    private bool _closing;
    private bool _canClose;

    public MainWindow(MainViewModel viewModel, ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _logger = logger;
        _fullscreen = new WindowFullscreenController(this);
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += OnClosed;
        viewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Left && !_viewModel.Player.IsFullscreen)
        {
            if (_viewModel.BackCommand.CanExecute(null)) _viewModel.BackCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F && _viewModel.CurrentPage is LibraryPageViewModel)
        {
            var search = FindDescendant<TextBox>(LibraryContent, element => element.Name == "SearchBox");
            if (search is not null) { search.Focus(); search.SelectAll(); e.Handled = true; }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && key == Key.F6 && !_viewModel.Player.IsFullscreen)
        {
            if (MainNavigation.IsKeyboardFocusWithin)
            {
                var content = _viewModel.IsPlayerPage ? (FrameworkElement)PlayerContent : LibraryContent;
                content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            else if (MainNavigation.SelectedItem is { } selected &&
                     MainNavigation.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item) item.Focus();
            else MainNavigation.Focus();
            e.Handled = true;
            return;
        }
        PlayerKeyboard.Handle(e, _viewModel.Player, _viewModel.CurrentPage is PlayerViewModel);
    }

    private static T? FindDescendant<T>(DependencyObject root, Predicate<T> match) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && match(element)) return element;
            if (FindDescendant(child, match) is T found) return found;
        }
        return null;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsFullscreen)) return;
        try { _fullscreen.SetFullscreen(_viewModel.Player.IsFullscreen); }
        catch (Win32Exception error)
        {
            _logger.LogError(error, "Could not change fullscreen mode");
            _viewModel.Player.IsFullscreen = false;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            await _viewModel.Settings.CancelAndWaitAsync();
            await (_viewModel.Store.RefreshCommand.ExecutionTask ?? Task.CompletedTask);
            var player = _viewModel.Player;
            await Task.WhenAll(player.OpenFileCommand.ExecutionTask ?? Task.CompletedTask,
                player.OpenLibraryFileCommand.ExecutionTask ?? Task.CompletedTask,
                player.StartOverFileCommand.ExecutionTask ?? Task.CompletedTask,
                player.PlayEpisodeCommand.ExecutionTask ?? Task.CompletedTask,
                player.PreviousEpisodeCommand.ExecutionTask ?? Task.CompletedTask,
                player.NextEpisodeCommand.ExecutionTask ?? Task.CompletedTask,
                player.Sidebar?.ToggleCommand.ExecutionTask ?? Task.CompletedTask,
                player.PlayPauseCommand.ExecutionTask ?? Task.CompletedTask,
                player.StopCommand.ExecutionTask ?? Task.CompletedTask,
                player.SelectAudioTrackCommand.ExecutionTask ?? Task.CompletedTask,
                player.SelectSubtitleTrackCommand.ExecutionTask ?? Task.CompletedTask,
                player.LoadSubtitleCommand.ExecutionTask ?? Task.CompletedTask,
                player.SelectSpeedCommand.ExecutionTask ?? Task.CompletedTask,
                player.SelectAspectRatioCommand.ExecutionTask ?? Task.CompletedTask);
            player.Playback.Pause();
            await player.FlushProgressAsync();
            await player.Playback.StopAsync();
            await player.FlushProgressAsync();
        }
        catch (Exception error) { _logger.LogError(error, "Error while stopping playback during shutdown"); }
        _canClose = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e) =>
        _viewModel.Player.PropertyChanged -= OnPlayerPropertyChanged;
}

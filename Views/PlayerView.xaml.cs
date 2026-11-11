using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player.Views;

public partial class PlayerView : UserControl
{
    private NativeVideoHost? _video;
    private PlayerViewModel? _viewModel;
    private readonly DispatcherTimer _idleTimer;
    private Point? _lastMousePosition;

    public PlayerView()
    {
        InitializeComponent();
        _idleTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnIdleTick, Dispatcher);
        _idleTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as PlayerViewModel;
        if (_viewModel is null) return;
        _viewModel.Playback.Changed += OnPlaybackChanged;
        _viewModel.PropertyChanged += OnPlayerPropertyChanged;
        _viewModel.SubtitleMenuRequested += OnSubtitleMenuRequested;
        _viewModel.NotifyInteraction();
        if (IsVisible) _idleTimer.Start();
        AttachVideoOutput();
    }

    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(AttachVideoOutput);
    }

    private void AttachVideoOutput()
    {
        if (!IsLoaded || _viewModel?.Playback is not PlaybackService playback || playback.MediaPlayer is null) return;
        if (_video is null)
        {
            // A transparent native child avoids WPF airspace while sharing the owner's animation.
            PlayerLayout.Children.Remove(PlayerOverlay);
            PlayerOverlay.DataContext = _viewModel;
            PlayerOverlay.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;
            _video = new NativeVideoHost(playback.MediaPlayer, PlayerOverlay) { DataContext = _viewModel };
            VideoHost.Children.Add(_video);
            IdleMessage.Visibility = Visibility.Collapsed;
        }
    }

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PlayerKeyboard.OwnsNavigationKeys(e.OriginalSource as DependencyObject)) return;
        ((UIElement)sender).Focus();
        _viewModel?.NotifyInteraction();
        if (e.ClickCount == 2 && _viewModel?.AreControlsLocked != true) _viewModel?.FullscreenCommand.Execute(null);
        e.Handled = true;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The controls have a separate presentation source; keep page visibility explicit.
        PlayerOverlay.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible && IsLoaded) { _viewModel?.NotifyInteraction(); _idleTimer.Start(); }
        else _idleTimer.Stop();
    }

    private void OnVideoKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        _viewModel?.NotifyInteraction();
        // The native child has its own presentation source and keyboard focus scope.
        if (e.Key == Key.Tab)
        {
            if (_viewModel?.AreControlsLocked == true)
            {
                UnlockControlsButton.Focus();
                e.Handled = true;
                return;
            }
            // Only transfer into controls when focus came from the video surface.
            if (PlayerKeyboard.OwnsNavigationKeys(e.OriginalSource as DependencyObject)) return;
            PlaybackControls.UpdateLayout();
            PlaybackControls.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            e.Handled = true;
            return;
        }
        if (_viewModel is not null) PlayerKeyboard.Handle(e, _viewModel, true);
    }

    private void OnPlayerInteraction(object sender, InputEventArgs e) => _viewModel?.NotifyInteraction();

    private void OnPlayerMouseMove(object sender, MouseEventArgs e)
    {
        // Ignore synthetic movement caused by layout changes as controls appear/disappear.
        var position = ((UIElement)sender).PointToScreen(e.GetPosition((IInputElement)sender));
        if (_lastMousePosition == position) return;
        _lastMousePosition = position;
        var local = e.GetPosition(PlayerOverlay);
        _viewModel?.NotifyPointerMovement(local.Y, PlayerOverlay.ActualHeight);
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        // A stationary pointer or focus left by clicking Play must not pin the overlay open.
        var usingControls = PlayerHeader.IsMouseCaptureWithin || PlaybackControls.IsMouseCaptureWithin ||
            EpisodePanel.IsMouseCaptureWithin ||
            DescendantMenuIsOpen(PlaybackControls) || DescendantMenuIsOpen(EpisodePanel);
        _viewModel?.UpdateControlsVisibility(usingControls);
    }

    private static bool DescendantMenuIsOpen(DependencyObject root)
    {
        if (root is ComboBox { IsDropDownOpen: true }) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (DescendantMenuIsOpen(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.AreControlsLocked) ||
            e.PropertyName == nameof(PlayerViewModel.IsFullscreen) && _viewModel?.IsFullscreen == true ||
            e.PropertyName == nameof(PlayerViewModel.AreControlsVisible) && _viewModel?.AreControlsVisible == false &&
            (PlayerHeader.IsKeyboardFocusWithin || PlaybackControls.IsKeyboardFocusWithin || EpisodePanel.IsKeyboardFocusWithin))
            PlayerOverlay.Focus();
        PlayerOverlay.Cursor = _viewModel?.AreControlsVisible == false ? Cursors.None : null;
    }

    private void OnSubtitleMenuRequested(object? sender, EventArgs e)
    {
        PlaybackControls.UpdateLayout();
        SubtitleSelector.Focus();
        SubtitleSelector.IsDropDownOpen = true;
    }

    private void OnLoadSubtitleClick(object sender, RoutedEventArgs e) => SubtitleSelector.IsDropDownOpen = false;
    private void OnSeekStarted(object? sender, EventArgs e) => _viewModel?.BeginSeek();
    private void OnSeekCompleted(object? sender, EventArgs e) => _viewModel?.CommitSeek();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _idleTimer.Stop();
        if (_viewModel is not null)
        {
            _viewModel.Playback.Changed -= OnPlaybackChanged;
            _viewModel.PropertyChanged -= OnPlayerPropertyChanged;
            _viewModel.SubtitleMenuRequested -= OnSubtitleMenuRequested;
        }
        _viewModel = null;
        if (_video is not null)
        {
            _video.Dispose();
            VideoHost.Children.Remove(_video);
            _video = null;
            PlayerOverlay.ClearValue(DataContextProperty);
            PlayerLayout.Children.Add(PlayerOverlay);
        }
        IdleMessage.Visibility = Visibility.Visible;
    }
}

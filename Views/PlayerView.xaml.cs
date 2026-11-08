using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using LibVLCSharp.WPF;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player.Views;

public partial class PlayerView : UserControl
{
    private VideoView? _video;
    private Grid? _inputOverlay;
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
            // Nonzero alpha is required for mouse input in LibVLC's supported WPF overlay.
            var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0)), Focusable = true };
            _inputOverlay = overlay;
            overlay.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;
            overlay.MouseLeftButtonDown += OnVideoMouseDown;
            overlay.PreviewKeyDown += OnVideoKeyDown;
            overlay.MouseMove += OnPlayerMouseMove;
            _video = new VideoView { Content = overlay, MediaPlayer = playback.MediaPlayer };
            VideoHost.Children.Add(_video);
            IdleMessage.Visibility = Visibility.Collapsed;
        }
    }

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).Focus();
        _viewModel?.NotifyInteraction();
        if (e.ClickCount == 2) _viewModel?.FullscreenCommand.Execute(null);
        e.Handled = true;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // VideoView uses an owned overlay window; hide its contents with the page as well.
        if (_inputOverlay is not null)
            _inputOverlay.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible && IsLoaded) { _viewModel?.NotifyInteraction(); _idleTimer.Start(); }
        else _idleTimer.Stop();
    }

    private void OnVideoKeyDown(object sender, KeyEventArgs e)
    {
        _viewModel?.NotifyInteraction();
        // VideoView's content lives in an owned window, outside the main window's focus scope.
        if (e.Key == Key.Tab)
        {
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
        _viewModel?.NotifyInteraction();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        var usingControls = PlayerHeader.IsMouseOver || PlayerHeader.IsKeyboardFocusWithin ||
            PlaybackControls.IsMouseOver || PlaybackControls.IsKeyboardFocusWithin ||
            DescendantMenuIsOpen(PlaybackControls);
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
        if (e.PropertyName == nameof(PlayerViewModel.IsFullscreen) && _viewModel?.IsFullscreen == true)
            _inputOverlay?.Focus();
        if (_inputOverlay is not null)
            _inputOverlay.Cursor = _viewModel?.AreControlsVisible == false ? Cursors.None : null;
    }

    private void OnSubtitleMenuRequested(object? sender, EventArgs e)
    {
        PlaybackOptionsPanel.IsExpanded = true;
        PlaybackControls.UpdateLayout();
        SubtitleSelector.Focus();
        SubtitleSelector.IsDropDownOpen = true;
    }

    private void OnSeekStarted(object sender, MouseButtonEventArgs e) => _viewModel?.BeginSeek();
    private void OnSeekCompleted(object sender, RoutedEventArgs e) => _viewModel?.CommitSeek();

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
            _video.MediaPlayer = null;
            _video.Dispose();
            VideoHost.Children.Remove(_video);
            _video = null;
            _inputOverlay = null;
        }
        IdleMessage.Visibility = Visibility.Visible;
    }
}

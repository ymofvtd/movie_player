using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player.Views;

public partial class EpisodeSidebar : UserControl
{
    private EpisodeSidebarViewModel? _sidebar;
    public EpisodeSidebar() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _sidebar = (DataContext as PlayerViewModel)?.Sidebar;
        if (_sidebar is not null) _sidebar.PropertyChanged += OnSidebarChanged;
        ScrollToActive();
    }

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EpisodeSidebarViewModel.ActiveEpisodeId) or nameof(EpisodeSidebarViewModel.SelectedSeason)
            or nameof(EpisodeSidebarViewModel.IsExpanded)) ScrollToActive();
    }

    private void ScrollToActive() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (IsLoaded && _sidebar?.Episodes.FirstOrDefault(e => e.IsActive) is { } active)
            EpisodeList.ScrollIntoView(active);
    });

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_sidebar is not null) _sidebar.PropertyChanged -= OnSidebarChanged;
        _sidebar = null;
    }
}

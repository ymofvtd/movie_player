using MyCinema.Core.Navigation;

namespace Person_Movie_Player.ViewModels;

public sealed record NavigationItem(NavigationDestination Destination, string Label, string Glyph);

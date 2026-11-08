using CommunityToolkit.Mvvm.ComponentModel;

namespace Person_Movie_Player.ViewModels;

public abstract class PageViewModel(string title, string description) : ObservableObject
{
    public virtual string Title { get; } = title;
    public virtual string Description { get; } = description;
}

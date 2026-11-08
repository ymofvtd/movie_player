namespace Person_Movie_Player.ViewModels;

public sealed class SectionViewModel(string title, string description, string glyph, string emptyTitle,
    string emptyDescription) : PageViewModel(title, description)
{
    public string Glyph { get; } = glyph;
    public string EmptyTitle { get; } = emptyTitle;
    public string EmptyDescription { get; } = emptyDescription;
}

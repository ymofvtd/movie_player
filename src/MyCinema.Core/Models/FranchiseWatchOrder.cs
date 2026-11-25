namespace MyCinema.Core.Models;

public sealed class FranchiseWatchOrder(
    IReadOnlyDictionary<long, LibraryItem> items,
    IReadOnlyDictionary<long, long> moviePlacements)
{
    private readonly ILookup<long, LibraryItem> _children = items.Values.ToLookup(i => i.ParentId ?? 0);

    public long? FindFranchise(long itemId)
    {
        var item = items.GetValueOrDefault(itemId);
        if (item is { ItemType: LibraryItemType.Episode, ParentId: { } seasonId } &&
            items.GetValueOrDefault(seasonId)?.ParentId is { } showId)
            item = items.GetValueOrDefault(showId);
        return item is { ParentId: { } parentId } && items.GetValueOrDefault(parentId)?.ItemType == LibraryItemType.Franchise
            ? parentId : null;
    }

    public IEnumerable<LibraryItem> Episodes(long showId) => Children(showId)
        .Where(i => i.ItemType == LibraryItemType.Season)
        .SelectMany(s => Children(s.Id).Where(i => i.ItemType == LibraryItemType.Episode));

    public IReadOnlyList<LibraryItem> Build(long franchiseId)
    {
        var members = Children(franchiseId).ToArray();
        var episodes = members.Where(i => i.ItemType == LibraryItemType.Show).ToDictionary(i => i.Id, i => Episodes(i.Id).ToArray());
        var episodeIds = episodes.Values.SelectMany(group => group).Select(i => i.Id).ToHashSet();
        var anchoredMovies = members.Where(i => i.ItemType == LibraryItemType.Movie &&
            moviePlacements.TryGetValue(i.Id, out var after) && episodeIds.Contains(after))
            .ToLookup(i => moviePlacements[i.Id]);
        var anchoredIds = anchoredMovies.SelectMany(group => group).Select(i => i.Id).ToHashSet();
        var order = new List<LibraryItem>();
        foreach (var member in members)
        {
            if (member.ItemType == LibraryItemType.Movie && !anchoredIds.Contains(member.Id)) order.Add(member);
            if (!episodes.TryGetValue(member.Id, out var showEpisodes)) continue;
            foreach (var episode in showEpisodes)
            {
                order.Add(episode);
                order.AddRange(anchoredMovies[episode.Id]);
            }
        }
        return order;
    }

    private IOrderedEnumerable<LibraryItem> Children(long id) => _children[id].OrderBy(i => i.SeasonNumber)
        .ThenBy(i => i.EpisodeNumber).ThenBy(i => i.SortTitle, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id);
}

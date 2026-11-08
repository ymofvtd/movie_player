using System.Text.RegularExpressions;

namespace MyCinema.Infrastructure.Scanning;

public sealed record ParsedMediaName(string Title, int? Year, string? Edition);
public sealed record ParsedEpisode(int Season, int Number, int? EndNumber, string? Title);

public static partial class MediaNameParser
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts" };

    public static int? Season(string name) => SeasonPattern().Match(name) is { Success: true } match &&
        int.TryParse(match.Groups[1].Value, out var number) ? number : null;

    public static ParsedEpisode? Episode(string name, int? knownSeason = null)
    {
        name = WithoutExtension(name);
        var match = EpisodePattern().Match(name);
        if (!match.Success && knownSeason.HasValue) match = ShortEpisodePattern().Match(name);
        if (!match.Success) return null;
        var season = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value) : knownSeason!.Value;
        var episode = int.Parse(match.Groups["e"].Value);
        int? end = match.Groups["end"].Success ? int.Parse(match.Groups["end"].Value) : null;
        if (end <= episode) end = null;
        var tail = Clean(name[(match.Index + match.Length)..]);
        return new(season, episode, end, string.IsNullOrWhiteSpace(tail.Title) ? null : tail.Title);
    }

    public static ParsedMediaName Clean(string name, bool collection = false)
    {
        name = WithoutExtension(name);
        string? edition = null;
        if (Extended().IsMatch(name)) edition = "Extended Edition";
        else if (DirectorCut().IsMatch(name)) edition = "Director's Cut";
        else if (Theatrical().IsMatch(name)) edition = "Theatrical Cut";
        else if (Remastered().IsMatch(name)) edition = "Remastered";
        var years = YearPattern().Matches(name);
        int? year = years.Count > 0 ? int.Parse(years[^1].Value) : null;
        if (years.Count > 0) name = name.Remove(years[^1].Index, years[^1].Length);
        name = EditionTags().Replace(name, " ");
        name = TechnicalTags().Replace(name, " ");
        name = Brackets().Replace(name, " ");
        name = OrdinalPrefix().Replace(name, "");
        name = name.Replace('.', ' ').Replace('_', ' ');
        name = Whitespace().Replace(name, " ").Trim(' ', '-', '(', ')', '[', ']');
        if (collection) name = CollectionSuffix().Replace(name, "").Trim(' ', '-');
        return new(name, year, edition);
    }

    public static bool IsExtra(string name) => ExtraPattern().IsMatch(WithoutExtension(name).Replace('_', ' ').Replace('.', ' '));

    public static string MovieIdentity(string name) => Whitespace().Replace(
        PartPattern().Replace(Clean(name).Title, ""), " ").Trim(' ', '-');

    public static string SortTitle(string name)
    {
        var ordinal = OrdinalPrefix().Match(name);
        return ordinal.Success ? ordinal.Value.TrimEnd(' ', '.', ')').PadLeft(10, '0') + " " + Clean(name).Title : Clean(name).Title;
    }

    private static string WithoutExtension(string name) => VideoExtensions.Contains(Path.GetExtension(name))
        ? Path.GetFileNameWithoutExtension(name) : name;

    [GeneratedRegex(@"^(?:Season|Series|S)[ ._-]*(\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();
    [GeneratedRegex(@"(?<![a-z0-9])(?:S(?<s>\d{1,3})[ ._-]*E(?<e>\d{1,4})(?:[ ._-]*E(?<end>\d{1,4}))?|(?<s>\d{1,3})x(?<e>\d{1,4})(?:-?(?:E|x)?(?<end>\d{1,4}))?)(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();
    [GeneratedRegex(@"(?<![a-z0-9])E(?<e>\d{1,4})(?:[ ._-]*E(?<end>\d{1,4}))?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex ShortEpisodePattern();
    [GeneratedRegex(@"\b(?:18|19|20)\d{2}\b")]
    private static partial Regex YearPattern();
    [GeneratedRegex(@"\bEXTENDED(?:[ ._-]+EDITION)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Extended();
    [GeneratedRegex(@"\bDIRECTOR['’]?S?[ ._-]+CUT\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectorCut();
    [GeneratedRegex(@"\bTHEATRICAL(?:[ ._-]+CUT)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Theatrical();
    [GeneratedRegex(@"\bREMASTERED\b", RegexOptions.IgnoreCase)]
    private static partial Regex Remastered();
    [GeneratedRegex(@"\b(?:EXTENDED(?:[ ._-]+EDITION)?|DIRECTOR['’]?S?[ ._-]+CUT|THEATRICAL(?:[ ._-]+CUT)?|REMASTERED)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionTags();
    [GeneratedRegex(@"\b(?:\d{3,4}p|4K|8K|Blu[ ._-]?Ray|BRRip|BDRip|WEB[ ._-]?(?:DL|Rip)|HDTV|DVD(?:Rip)?|[xh][ ._-]?26[45]|HEVC|AVC|HDR(?:10)?|DTS|AAC|DDP?\d?|AC3|MULTI)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTags();
    [GeneratedRegex(@"\[[^\]]*\]|\(\s*\)")]
    private static partial Regex Brackets();
    [GeneratedRegex(@"^\d+[.)]\s+")]
    private static partial Regex OrdinalPrefix();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"(?:[ -]+)?(?:Complete|Collection|Complete Collection|Complete Trilogy)$", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionSuffix();
    [GeneratedRegex(@"(?:^|[ -])(?:samples?|trailers?|extras?|featurettes?|bonus(?: features)?|behind the scenes)(?:$|[ -])", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraPattern();
    [GeneratedRegex(@"\b(?:part|cd|disc|disk)[ -]*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartPattern();
}

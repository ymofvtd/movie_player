using MyCinema.Core.Models;

namespace MyCinema.Core.Scanning;

public sealed record ScanProgress(string Stage, string Path, int Directories, int Files);
public sealed record ScanWarning(string Path, string Message);
public sealed record ScanResult(int CompletedRoots, int Files, int Items, IReadOnlyList<ScanWarning> Warnings);

// Items are ordered parent first. Source keys identify folders/files, independently of display metadata.
public sealed record ScannedItem(string SourceKey, string? ParentKey, LibraryItem Item);
public sealed record ScannedFile(string ItemKey, string Path, long Size, DateTimeOffset ModifiedUtc,
    string? Edition);
public sealed record RootScan(IReadOnlyList<ScannedItem> Items, IReadOnlyList<ScannedFile> Files,
    IReadOnlyList<string> UnreadablePaths, IReadOnlyList<ScanWarning> Warnings);

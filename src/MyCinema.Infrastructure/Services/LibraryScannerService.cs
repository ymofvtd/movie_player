using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Scanning;
using MyCinema.Infrastructure.Database;
using MyCinema.Infrastructure.Scanning;

namespace MyCinema.Infrastructure.Services;

public sealed class LibraryScannerService(ILibraryRepository library, IScanRepository scans,
    ILogger<LibraryScannerService> logger) : ILibraryScannerService, IDisposable
{
    public const string DefaultRoot = @"D:\Entertaiment";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ConfigureDefaultRootAsync(CancellationToken cancellationToken = default)
    {
        var roots = await library.GetRootsAsync(cancellationToken).ConfigureAwait(false);
        if (roots.Count == 0)
            await library.SaveRootAsync(new LibraryRoot { Path = DefaultRoot }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A library scan is already running.");
        try
        {
            var roots = await library.GetRootsAsync(cancellationToken).ConfigureAwait(false);
            var warnings = new List<ScanWarning>();
            var completed = 0;
            var files = 0;
            var items = 0;
            var covered = new List<string>();
            foreach (var root in roots.Where(r => r.IsEnabled).OrderBy(r => r.Path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonical = Sql.PathKey(root.Path);
                if (covered.Any(parent => canonical.StartsWith(
                    Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                {
                    warnings.Add(new(root.Path, "Skipped an overlapping root; its files are included in a parent library folder."));
                    continue;
                }
                logger.LogInformation("Scanning library root {Path}", root.Path);
                var scan = await Task.Run(() => new FolderScanner().Read(root.Path, progress, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                warnings.AddRange(scan.Warnings);
                if (scan.UnreadablePaths.Any(p => Sql.PathKey(p) == Sql.PathKey(root.Path)))
                {
                    logger.LogWarning("Library root unavailable; retained its saved records: {Path}", root.Path);
                    continue;
                }
                await scans.CommitAsync(root.Id, scan, cancellationToken).ConfigureAwait(false);
                covered.Add(canonical);
                completed++;
                files += scan.Files.Count;
                items += scan.Items.Count;
                logger.LogInformation("Saved root {Path}: {Files} files, {Items} items, {Warnings} warnings",
                    root.Path, scan.Files.Count, scan.Items.Count, scan.Warnings.Count);
            }
            return new(completed, files, items, warnings);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}

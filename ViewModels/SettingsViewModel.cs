using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Scanning;
using Person_Movie_Player.Services;

namespace Person_Movie_Player.ViewModels;

public sealed record ScanPreviewItem(string Label, IReadOnlyList<ScanPreviewItem> Children);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IApplicationPaths _paths;
    private readonly ILibraryRepository _library;
    private readonly ILibraryScannerService _scanner;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(IApplicationPaths paths, ILibraryRepository library, ILibraryScannerService scanner,
        IUiDispatcher dispatcher, ILogger<SettingsViewModel> logger)
        : base("Settings", "Your cinema, on your computer.")
    {
        _paths = paths;
        _library = library;
        _scanner = scanner;
        _dispatcher = dispatcher;
        _logger = logger;
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        CancelScanCommand = new RelayCommand(() =>
        {
            ScanCommand.Cancel();
            ScanStatus = "Canceling scan…";
            CancelScanCommand!.NotifyCanExecuteChanged();
        }, () => IsScanning && !ScanCommand.IsCancellationRequested);
    }

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
    public string ApplicationDataDirectory => _paths.RootDirectory;
    public string LogsDirectory => _paths.LogsDirectory;
    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelScanCommand { get; }
    public event EventHandler? LibraryChanged;

    [ObservableProperty] private string _libraryRoots = "Loading library folders…";
    [ObservableProperty] private string _scanStatus = "Ready to scan. Your media stays in its current folders.";
    [ObservableProperty] private string _scanPath = "";
    [ObservableProperty] private string _librarySummary = "No media scanned yet.";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private IReadOnlyList<ScanPreviewItem> _previewItems = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasScanWarnings))]
    private IReadOnlyList<string> _scanWarnings = [];
    public bool HasScanWarnings => ScanWarnings.Count > 0;

    partial void OnIsScanningChanged(bool value) => CancelScanCommand.NotifyCanExecuteChanged();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _scanner.ConfigureDefaultRootAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        ScanWarnings = [];
        ScanStatus = "Starting library scan…";
        var progress = new UiScanProgress(value => _dispatcher.Post(() =>
        {
            if (!IsScanning || cancellationToken.IsCancellationRequested) return;
            ScanStatus = $"{value.Stage} · {value.Files} videos · {value.Directories} folders";
            ScanPath = value.Path;
        }));
        try
        {
            var result = await _scanner.ScanAsync(progress, cancellationToken);
            ScanWarnings = result.Warnings.Select(w => $"{w.Path}: {w.Message}").ToArray();
            ScanStatus = result.CompletedRoots == 0
                ? "No folders scanned. Check that your library drive is connected and the folders are enabled."
                : $"Scan complete · {result.Files} videos · {result.Warnings.Count} warnings";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ScanStatus = "Scan canceled. Completed folders were saved; the unfinished folder was left unchanged.";
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Library scan failed");
            ScanStatus = "Scan failed. " + error.Message;
        }
        finally
        {
            IsScanning = false;
            ScanPath = "";
            try
            {
                await RefreshAsync();
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not reload scan summary");
                ScanStatus += " Could not reload the library summary.";
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var roots = await _library.GetRootsAsync(cancellationToken);
        LibraryRoots = string.Join(Environment.NewLine, roots.Select(root =>
            $"{root.Path}{(root.IsEnabled ? "" : " (disabled)")}\n" +
            (root.LastScanUtc is { } utc ? $"Last successful scan: {utc.ToLocalTime():g}" : "Not scanned yet")));
        var items = await _library.GetItemsAsync(cancellationToken: cancellationToken);
        var files = await _library.GetAllFilesAsync(cancellationToken);
        var byId = items.ToDictionary(i => i.Id);
        var visible = new HashSet<long>();
        foreach (var file in files.Where(f => f.IsAvailable))
        {
            long? id = file.LibraryItemId;
            while (id.HasValue && visible.Add(id.Value) && byId.TryGetValue(id.Value, out var item)) id = item.ParentId;
        }
        var active = items.Where(i => visible.Contains(i.Id)).ToArray();
        int Count(LibraryItemType type) => active.Count(i => i.ItemType == type);
        LibrarySummary = $"{files.Count(f => f.IsAvailable)} videos · {Count(LibraryItemType.Movie)} movies · " +
            $"{Count(LibraryItemType.Show)} shows · {Count(LibraryItemType.Franchise)} franchise candidates · " +
            $"{Count(LibraryItemType.Uncategorized)} uncategorized · {files.Count(f => !f.IsAvailable)} unavailable";
        var children = active.ToLookup(i => i.ParentId ?? 0);
        ScanPreviewItem Build(LibraryItem item) => new(
            $"{item.Title}{(item.ReleaseYear.HasValue ? $" ({item.ReleaseYear})" : "")} · " +
                (item.ItemType == LibraryItemType.Franchise ? "Franchise candidate" : item.ItemType.ToString()),
            children[item.Id].OrderBy(i => i.SeasonNumber).ThenBy(i => i.EpisodeNumber)
                .ThenBy(i => i.ReleaseYear).ThenBy(i => i.SortTitle).Select(Build).ToArray());
        PreviewItems = active.Where(i => i.ParentId is null || !visible.Contains(i.ParentId.Value))
            .OrderBy(i => i.Title).Select(Build).ToArray();
    }

    public async Task CancelAndWaitAsync()
    {
        ScanCommand.Cancel();
        if (ScanCommand.ExecutionTask is { } task) await task;
    }

    private sealed class UiScanProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Infrastructure.Services;

namespace Person_Movie_Player.Services;

public sealed partial class LibraryStore : ObservableObject, IDisposable
{
    private readonly ILibraryRepository _repository;
    private readonly ILogger<LibraryStore> _logger;
    private readonly WatchHistoryService? _history;
    private readonly IUiDispatcher? _dispatcher;
    private bool _refreshAgain;
    private bool _disposed;
    private TaskCompletionSource? _refreshCompletion;
    public LibraryStore(ILibraryRepository repository, ILogger<LibraryStore> logger,
        WatchHistoryService? history = null, IUiDispatcher? dispatcher = null)
    {
        _repository = repository;
        _logger = logger;
        _history = history;
        _dispatcher = dispatcher;
        if (history is not null) history.Saved += OnHistorySaved;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }
    [ObservableProperty] private LibraryCatalog _catalog = new(LibrarySnapshot.Empty);
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";
    public IAsyncRelayCommand RefreshCommand { get; }
    public event EventHandler? Changed;

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (IsLoading)
        {
            _refreshAgain = true;
            if (_refreshCompletion is { } pending) await pending.Task;
            return;
        }
        IsLoading = true;
        var completion = _refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ErrorMessage = "";
        try
        {
            do
            {
                _refreshAgain = false;
                var snapshot = await _repository.GetSnapshotAsync();
                Catalog = await Task.Run(() => new LibraryCatalog(snapshot));
                if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
            } while (_refreshAgain && !_disposed);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not load library browser");
            ErrorMessage = "Could not load the library. Your saved data is unchanged. Try Refresh.";
        }
        finally { IsLoading = false; completion.TrySetResult(); }
    }

    private void OnHistorySaved(object? sender, EventArgs e)
    {
        void Refresh() { if (!_disposed && _history?.Warning is null) _ = RefreshAsync(); }
        if (_dispatcher is not null) _dispatcher.Post(Refresh);
        else Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        if (_history is not null) _history.Saved -= OnHistorySaved;
    }
}

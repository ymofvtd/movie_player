using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Navigation;

namespace Person_Movie_Player.Services;

public sealed class NavigationService(ILogger<NavigationService> logger) : ObservableObject, INavigationService
{
    private readonly Stack<(NavigationDestination Destination, long? ItemId, long? FileId)> _history = new();
    private NavigationDestination _currentDestination = NavigationDestination.Home;

    public NavigationDestination CurrentDestination => _currentDestination;
    public long? CurrentItemId { get; private set; }
    public long? CurrentFileId { get; private set; }
    public bool CanGoBack => _history.Count > 0;

    public void NavigateTo(NavigationDestination destination)
    {
        if (!Enum.IsDefined(destination))
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        if (destination == _currentDestination)
        {
            return;
        }

        if (destination == NavigationDestination.Details)
            throw new ArgumentException("Use NavigateToItem to open details.", nameof(destination));
        _history.Push((_currentDestination, CurrentItemId, CurrentFileId));
        SetDestination(destination, null);
    }

    public void NavigateToItem(long itemId, long? fileId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        if (_currentDestination == NavigationDestination.Details && CurrentItemId == itemId && CurrentFileId == fileId) return;
        _history.Push((_currentDestination, CurrentItemId, CurrentFileId));
        SetDestination(NavigationDestination.Details, itemId, fileId);
    }

    public void GoBack()
    {
        if (_history.TryPop(out var destination))
        {
            SetDestination(destination.Destination, destination.ItemId, destination.FileId);
        }
    }

    private void SetDestination(NavigationDestination destination, long? itemId, long? fileId = null)
    {
        _currentDestination = destination;
        CurrentItemId = itemId;
        CurrentFileId = fileId;
        logger.LogInformation("Navigated to {Destination}", destination);
        OnPropertyChanged(nameof(CurrentDestination));
        OnPropertyChanged(nameof(CurrentItemId));
        OnPropertyChanged(nameof(CanGoBack));
    }
}

using System.ComponentModel;
using MyCinema.Core.Navigation;

namespace MyCinema.Core.Interfaces;

public interface INavigationService : INotifyPropertyChanged
{
    NavigationDestination CurrentDestination { get; }
    long? CurrentItemId { get; }
    long? CurrentFileId { get; }
    bool CanGoBack { get; }
    void NavigateTo(NavigationDestination destination);
    void NavigateToItem(long itemId, long? fileId = null);
    void GoBack();
}

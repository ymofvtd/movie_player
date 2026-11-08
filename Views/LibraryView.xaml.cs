using System.Windows.Controls;
using System.Windows.Input;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is LibraryPageViewModel page && page.SearchText.Length > 0)
        {
            page.SearchText = "";
            e.Handled = true;
        }
    }
}

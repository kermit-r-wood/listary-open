using System.Windows;

namespace ListaryOpen.App;

public partial class SearchPanel : Window
{
    public SearchPanel()
    {
        InitializeComponent();
    }

    public void ActivateSearch()
    {
        Show();
        Activate();
        QueryBox.Focus();
    }
}

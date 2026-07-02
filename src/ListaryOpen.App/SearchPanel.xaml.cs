using System.ComponentModel;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}

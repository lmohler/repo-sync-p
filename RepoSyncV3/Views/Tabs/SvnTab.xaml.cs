using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views.Tabs;

public partial class SvnTab : UserControl
{
    public SvnTab() => InitializeComponent();

    private void TreeList_DoubleClick(object sender, MouseButtonEventArgs e) => FileTreeInput.ToggleSelectedFolder((ListBox)sender);

    private void TreeList_PreviewKeyDown(object sender, KeyEventArgs e) => FileTreeInput.PreviewKeyDown((ListBox)sender, e);

    /// <summary>Double-clicking a revision opens its git mirror commit on the History tab.</summary>
    private void Revisions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RepoDetailViewModel detail && detail.SvnGoToMirrorCommand.CanExecute(null))
            detail.SvnGoToMirrorCommand.Execute(null);
    }
}

using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views.Tabs;

/// <summary>DataContext: RepoDetailViewModel.</summary>
public partial class BranchesTab : UserControl
{
    public BranchesTab() => InitializeComponent();

    private void LocalBranch_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalBranchGrid.SelectedItem is BranchInfo { IsCurrent: false } b && DataContext is RepoDetailViewModel d && d.SwitchBranchCommand.CanExecute(b))
            d.SwitchBranchCommand.Execute(b);
    }

    private void RemoteBranch_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteBranchGrid.SelectedItem is BranchInfo b && DataContext is RepoDetailViewModel d && d.CheckoutRemoteCommand.CanExecute(b))
            d.CheckoutRemoteCommand.Execute(b);
    }
}

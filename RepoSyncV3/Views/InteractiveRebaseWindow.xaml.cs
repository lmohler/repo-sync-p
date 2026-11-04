using System.Collections.ObjectModel;
using System.Windows;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

public partial class InteractiveRebaseWindow : Window
{
    public ObservableCollection<RebaseStep> Plan { get; }

    public InteractiveRebaseWindow(string branch, string onto, IEnumerable<RebaseStep> steps, bool anyPushed)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        Plan = new ObservableCollection<RebaseStep>(steps);
        Steps.ItemsSource = Plan;
        Subtitle.Text = $"{Plan.Count} commit(s) on '{branch}' since {onto}.";
        PushedWarning.Visibility = anyPushed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Move(int delta)
    {
        Steps.CommitEdit();
        if (Steps.SelectedItem is not RebaseStep s) return;
        var i = Plan.IndexOf(s);
        var j = i + delta;
        if (j < 0 || j >= Plan.Count) return;
        Plan.Move(i, j);
        Steps.SelectedItem = s;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Steps.CommitEdit();
        var first = Plan.FirstOrDefault(p => p.Action != "drop");
        if (first is { Action: "squash" or "fixup" })
        {
            MessageBox.Show(this, "The first kept commit can't be squash or fixup - there is nothing above it to melt into.", "Interactive rebase");
            return;
        }
        if (Plan.All(p => p.Action == "drop"))
        {
            MessageBox.Show(this, "Every commit is set to drop. Use Reset on the History tab if you really want that.", "Interactive rebase");
            return;
        }
        DialogResult = true;
    }
}

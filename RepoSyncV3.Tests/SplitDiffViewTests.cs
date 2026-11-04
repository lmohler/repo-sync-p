using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Models;
using RepoSyncV3.Views;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>
/// Long lines used to stretch the split rows past the viewport, pushing the right (new) side out of view.
/// Both panes must stay inside the control however long the lines are.
/// </summary>
public class SplitDiffViewTests
{
    private static List<DiffLine> LongDiff()
    {
        var longText = new string('x', 600);
        return new List<DiffLine>
        {
            new() { Text = "@@ -1,3 +1,3 @@", Kind = DiffKind.Hunk },
            new() { Text = " context " + longText, Kind = DiffKind.Context, OldNo = "1", NewNo = "1" },
            new() { Text = "-old " + longText, Kind = DiffKind.Delete, OldNo = "2" },
            new() { Text = "+new " + longText + longText, Kind = DiffKind.Add, NewNo = "2" },
            new() { Text = "+added", Kind = DiffKind.Add, NewNo = "3" },
        };
    }

    [Fact]
    public void Both_Panes_Stay_In_View_With_Long_Lines()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                XamlLoadTests.EnsureApp();
                var view = new SplitDiffView { ItemsSource = SideBySideDiffRow.FromUnified(LongDiff()) };
                view.Measure(new Size(1000, 400));
                view.Arrange(new Rect(0, 0, 1000, 400));
                view.UpdateLayout();

                var left = (ListBox)view.FindName("LeftList");
                var right = (ListBox)view.FindName("RightList");
                Assert.InRange(left.ActualWidth, 400, 600);
                Assert.InRange(right.ActualWidth, 400, 600);
                var rightOrigin = right.TranslatePoint(new Point(0, 0), view);
                Assert.True(rightOrigin.X + right.ActualWidth <= 1000.5, $"right pane ends at {rightOrigin.X + right.ActualWidth}");
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }

    [Fact]
    public void DiffWindow_Loads()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                XamlLoadTests.EnsureApp();
                var w = new DiffWindow("src/file.cs", "repo - abc1234 subject", LongDiff(), split: true);
                w.Measure(new Size(1300, 800));
                w.Arrange(new Rect(0, 0, 1300, 800));
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }
}

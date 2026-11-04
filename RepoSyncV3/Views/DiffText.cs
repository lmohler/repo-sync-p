using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>
/// <c>v:DiffText.Line="{Binding}"</c> on a TextBlock draws a diff line with syntax colours and the changed words
/// highlighted (see <see cref="DiffDecor"/>). Colours are theme resources, so they follow light / dark.
/// </summary>
public static class DiffText
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(DiffLine), typeof(DiffText), new PropertyMetadata(null, OnLineChanged));

    public static DiffLine? GetLine(DependencyObject d) => (DiffLine?)d.GetValue(LineProperty);
    public static void SetLine(DependencyObject d, DiffLine? value) => d.SetValue(LineProperty, value);

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not DiffLine line) return;
        foreach (var seg in DiffDecor.Segments(line))
        {
            var run = new Run(seg.Text);
            var fg = seg.Token switch
            {
                TokenKind.Keyword => "Brush.Syntax.Keyword",
                TokenKind.String => "Brush.Syntax.String",
                TokenKind.Comment => "Brush.Syntax.Comment",
                TokenKind.Number => "Brush.Syntax.Number",
                _ => null,
            };
            if (fg is not null) run.SetResourceReference(TextElement.ForegroundProperty, fg);
            if (seg.Changed)
                run.SetResourceReference(TextElement.BackgroundProperty, line.Kind == DiffKind.Add ? "Brush.Diff.AddWord" : "Brush.Diff.DeleteWord");
            tb.Inlines.Add(run);
        }
    }
}

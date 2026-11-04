using System.Windows;
using System.Windows.Media;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Draws one <see cref="GraphRow"/> of the history graph: lines first, then the commit dot on top.
/// Each branch line keeps one color; merge commits are hollow rings.</summary>
public sealed class GraphCell : FrameworkElement
{
    private const double LaneWidth = 16;
    private const int MaxLanes = 14;
    private const double LineWidth = 2.2;
    private const double DotRadius = 4.6;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(GraphRow), typeof(GraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public GraphRow? Row { get => (GraphRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    // distinct hues that read on both the dark and light themes; shared with the history ref labels
    public static readonly Brush[] Colors =
    {
        Freeze("#4C9AFF"), Freeze("#F2994A"), Freeze("#3DDC97"), Freeze("#C084FC"), Freeze("#F6C343"),
        Freeze("#FF6B8A"), Freeze("#2DD4BF"), Freeze("#A3E635"), Freeze("#F472B6"), Freeze("#94A3B8"),
    };

    private static readonly Pen[] Pens = Colors.Select(b =>
    {
        var p = new Pen(b, LineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }).ToArray();

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>The brush of graph line color <paramref name="index"/> (wraps around the palette).</summary>
    public static Brush ColorAt(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];

    public GraphCell() => ClipToBounds = false;

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(MaxLanes, Row?.Width ?? 1) * LaneWidth + 6, 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (Row is null) return;
        var h = ActualHeight;
        static double X(int lane) => lane * LaneWidth + LaneWidth / 2 + 3;
        // run the row's lines 1px past its edges so neighbouring rows join without a seam
        double Y(double y) => y == 0 ? -1 : y == 1 ? h + 1 : y * h;

        for (var i = 0; i < Row.Segments.Count; i++)
        {
            var (x1, y1, x2, y2) = Row.Segments[i];
            if (x1 >= MaxLanes || x2 >= MaxLanes) continue;
            var pen = Pens[Row.SegmentColors[i] % Pens.Length];
            var a = new Point(X(x1), Y(y1));
            var b = new Point(X(x2), Y(y2));
            if (x1 == x2)
            {
                dc.DrawLine(pen, a, b);
                continue;
            }
            // smooth bend between lanes
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(a, false, false);
                var midY = (a.Y + b.Y) / 2;
                ctx.BezierTo(new Point(a.X, midY), new Point(b.X, midY), b, true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        if (Row.Lane >= MaxLanes) return;
        var color = Colors[Row.DotColor % Colors.Length];
        var center = new Point(X(Row.Lane), h / 2);
        if (Row.IsMerge)
        {
            var background = TryFindResource("Brush.PanelBackground") as Brush ?? Brushes.Transparent;
            dc.DrawEllipse(background, new Pen(color, 2.2), center, DotRadius - 0.4, DotRadius - 0.4);
        }
        else dc.DrawEllipse(color, null, center, DotRadius, DotRadius);
    }
}

/// <summary>Graph color index -> the same brush the graph line uses, so a commit's ref labels match its line.</summary>
public sealed class GraphColorConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is int i ? GraphCell.ColorAt(i) : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

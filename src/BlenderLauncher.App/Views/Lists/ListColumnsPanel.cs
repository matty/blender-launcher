using Avalonia;
using Avalonia.Controls;

namespace BlenderLauncher.App.Views.Lists;

/// <summary>Which column set a <see cref="ListColumnsPanel"/> lays out.</summary>
public enum ListColumnsLayout
{
    /// <summary>Star, Version, Channel, Branch · commit, Built, Size, action.</summary>
    Installed,

    /// <summary>Version, Branch, Commit, Built, Size, action.</summary>
    Download,
}

/// <summary>
/// The lists' column layout: one row of cells in the design's columns (fixed widths and <c>fr</c> shares with minimums,
/// gap 10), used by the column header and every row so they always line up. Lighter than a <see cref="Grid"/> per row.
/// When the list is too narrow for every column (the default 1280-wide window with the detail panel open is), the
/// least important columns are dropped, in a fixed order, instead of clipping the action column.
/// </summary>
/// <remarks>
/// Children set <see cref="ColumnProperty"/> (and <see cref="ColumnSpanProperty"/>). A child whose columns are all
/// dropped is arranged empty and made transparent (render-only, so no layout loop).
/// </remarks>
public sealed class ListColumnsPanel : Panel
{
    public const double Gap = 10;

    public static readonly StyledProperty<ListColumnsLayout> LayoutProperty =
        AvaloniaProperty.Register<ListColumnsPanel, ListColumnsLayout>(nameof(Layout));

    public static readonly AttachedProperty<int> ColumnProperty =
        AvaloniaProperty.RegisterAttached<ListColumnsPanel, Control, int>("Column");

    public static readonly AttachedProperty<int> ColumnSpanProperty =
        AvaloniaProperty.RegisterAttached<ListColumnsPanel, Control, int>("ColumnSpan", 1);

    // Width (fixed) or Star (share) with Min; Drop: the order columns are dropped in when narrow (0 = never).
    private static readonly ColumnSpec[] InstalledColumns =
    [
        new(Width: 28),
        new(Star: 1.4, Min: 100),
        new(Width: 100, Drop: 2),
        new(Star: 1.3, Min: 120, Drop: 3),
        new(Width: 88, Drop: 4),
        new(Width: 60, Drop: 1),
        new(Width: 156),
    ];

    private static readonly ColumnSpec[] DownloadColumns =
    [
        new(Star: 1.3, Min: 110),
        new(Star: 1.3, Min: 80, Drop: 3),
        new(Width: 96, Drop: 1),
        new(Width: 88, Drop: 4),
        new(Width: 60, Drop: 2),
        new(Width: 156),
    ];

    private readonly HashSet<Control> _hidden = new(ReferenceEqualityComparer.Instance);
    private double[] _widths = [];
    private double _laidOutFor = double.NaN;

    static ListColumnsPanel()
    {
        AffectsMeasure<ListColumnsPanel>(LayoutProperty);
        AffectsParentMeasure<ListColumnsPanel>(ColumnProperty, ColumnSpanProperty);
    }

    public ListColumnsLayout Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static int GetColumn(Control control) => control.GetValue(ColumnProperty);

    public static void SetColumn(Control control, int value) => control.SetValue(ColumnProperty, value);

    public static int GetColumnSpan(Control control) => control.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(Control control, int value) => control.SetValue(ColumnSpanProperty, value);

    /// <summary>The column widths for <paramref name="width"/>; 0 for a dropped column. Public for tests.</summary>
    public static double[] ComputeWidths(ListColumnsLayout layout, double width)
    {
        var specs = layout == ListColumnsLayout.Installed ? InstalledColumns : DownloadColumns;
        var visible = new bool[specs.Length];
        Array.Fill(visible, true);

        if (!double.IsInfinity(width))
        {
            var dropOrder = Enumerable.Range(0, specs.Length).Where(i => specs[i].Drop > 0).OrderBy(i => specs[i].Drop).ToList();
            foreach (var drop in dropOrder)
            {
                if (MinimumWidth(specs, visible) <= width)
                {
                    break;
                }

                visible[drop] = false;
            }
        }

        var widths = new double[specs.Length];
        var starSpace = double.IsInfinity(width) ? 0 : width - Gaps(visible);
        var stars = new List<int>();
        for (var i = 0; i < specs.Length; i++)
        {
            if (!visible[i])
            {
                continue;
            }

            if (specs[i].Star > 0)
            {
                stars.Add(i);
            }
            else
            {
                widths[i] = specs[i].Width;
                starSpace -= specs[i].Width;
            }
        }

        // Share the rest by weight; a share below its minimum is pinned there and the rest re-shared.
        while (stars.Count > 0)
        {
            var weight = stars.Sum(i => specs[i].Star);
            var pinned = stars.FirstOrDefault(i => starSpace * specs[i].Star / weight < specs[i].Min, -1);
            if (pinned < 0)
            {
                foreach (var i in stars)
                {
                    widths[i] = starSpace * specs[i].Star / weight;
                }

                break;
            }

            widths[pinned] = specs[pinned].Min;
            starSpace -= specs[pinned].Min;
            stars.Remove(pinned);
        }

        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureWidths(availableSize.Width);
        var height = 0d;
        foreach (var child in Children)
        {
            var (_, width) = Slot(child);
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var total = 0d;
        var columns = 0;
        foreach (var w in _widths)
        {
            if (w > 0)
            {
                total += w;
                columns++;
            }
        }

        return new Size(total + (Gap * Math.Max(0, columns - 1)), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width != _laidOutFor)
        {
            EnsureWidths(finalSize.Width);
            foreach (var child in Children)
            {
                child.Measure(new Size(Slot(child).Width, finalSize.Height));
            }
        }

        foreach (var child in Children)
        {
            var (x, width) = Slot(child);
            if (width <= 0)
            {
                child.Arrange(default);
                if (_hidden.Add(child))
                {
                    child.Opacity = 0;
                }

                continue;
            }

            if (_hidden.Remove(child))
            {
                child.Opacity = 1;
            }

            child.Arrange(new Rect(x, 0, width, finalSize.Height));
        }

        return finalSize;
    }

    private void EnsureWidths(double width)
    {
        if (width == _laidOutFor && _widths.Length > 0)
        {
            return;
        }

        _widths = ComputeWidths(Layout, width);
        _laidOutFor = width;
    }

    /// <summary>The left edge and width of a child's columns (dropped columns take no space).</summary>
    private (double X, double Width) Slot(Control child)
    {
        var column = Math.Clamp(GetColumn(child), 0, _widths.Length - 1);
        var end = Math.Min(_widths.Length, column + Math.Max(1, GetColumnSpan(child)));
        var x = 0d;
        for (var i = 0; i < column; i++)
        {
            if (_widths[i] > 0)
            {
                x += _widths[i] + Gap;
            }
        }

        var width = 0d;
        var spanned = 0;
        for (var i = column; i < end; i++)
        {
            if (_widths[i] > 0)
            {
                width += _widths[i];
                spanned++;
            }
        }

        return (x, width + (Gap * Math.Max(0, spanned - 1)));
    }

    private static double MinimumWidth(ColumnSpec[] specs, bool[] visible)
    {
        var total = Gaps(visible);
        for (var i = 0; i < specs.Length; i++)
        {
            if (visible[i])
            {
                total += specs[i].Star > 0 ? specs[i].Min : specs[i].Width;
            }
        }

        return total;
    }

    private static double Gaps(bool[] visible) => Gap * Math.Max(0, visible.Count(v => v) - 1);

    private sealed record ColumnSpec(double Width = 0, double Star = 0, double Min = 0, int Drop = 0);
}

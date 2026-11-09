using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// A Markdown table in the column that it has. Columns keep their natural widths while the table fits. When it does not, the widest columns give way
/// and their text wraps. Only a table that cannot fit at the least column width scrolls sideways.
/// </summary>
internal sealed class FitTable : Decorator
{
    // The least width of a column of wrapped text, and the table's left and top border.
    private const double MinColumn = 64;
    private const double Edge = 1;

    private readonly Grid _grid;
    private double _fittedTo = -1;

    public FitTable(Grid grid)
    {
        _grid = grid;
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
            Classes = { "mdScroll" },
            Content = new Border { Classes = { "mdTable" }, Child = grid },
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && Math.Abs(availableSize.Width - _fittedTo) >= 0.5)
        {
            _fittedTo = availableSize.Width;
            Fit(availableSize.Width);
        }

        return base.MeasureOverride(availableSize);
    }

    private void Fit(double width)
    {
        foreach (var column in _grid.ColumnDefinitions)
        {
            column.Width = GridLength.Auto;
        }

        var natural = new double[_grid.ColumnDefinitions.Count];
        foreach (var cell in _grid.Children)
        {
            cell.Measure(Size.Infinity);
            var at = Grid.GetColumn(cell);
            natural[at] = Math.Max(natural[at], cell.DesiredSize.Width);
        }

        foreach (var (column, size) in _grid.ColumnDefinitions.Zip(Widths(natural, width - Edge)))
        {
            column.Width = new GridLength(size);
        }
    }

    /// <summary>Each column's width: its natural width while all fit, and otherwise narrower in proportion to how far it can give way.</summary>
    internal static double[] Widths(double[] natural, double room)
    {
        var over = natural.Sum() - room;
        var flexible = natural.Sum(width => Math.Max(0, width - MinColumn));
        if (over <= 0 || flexible <= 0)
        {
            return natural;
        }

        var share = Math.Min(1, over / flexible);
        return [.. natural.Select(width => width - Math.Max(0, width - MinColumn) * share)];
    }
}

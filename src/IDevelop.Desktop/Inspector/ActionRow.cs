using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// A row of buttons with one rule at every width: all on one line, each at its own width, when they fit, else the first
/// across the whole width and the rest sharing the line under it equally. A hidden button takes no place.
/// </summary>
public sealed class ActionRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ActionRow, double>(nameof(Spacing), 8);

    static ActionRow() => AffectsMeasure<ActionRow>(SpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Whether the last layout put every button on one line.</summary>
    public bool IsOneLine { get; private set; } = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Shown();
        foreach (var child in shown)
        {
            child.Measure(Size.Infinity);
        }

        var oneLine = shown.Sum(child => child.DesiredSize.Width) + Spacing * Math.Max(0, shown.Length - 1);
        IsOneLine = shown.Length < 2 || oneLine <= availableSize.Width;
        if (IsOneLine)
        {
            return new Size(oneLine, shown.Length == 0 ? 0 : shown.Max(child => child.DesiredSize.Height));
        }

        var width = availableSize.Width;
        shown[0].Measure(new Size(width, double.PositiveInfinity));
        var rest = shown[1..];
        var share = Share(width, rest.Length);
        foreach (var child in rest)
        {
            child.Measure(new Size(share, double.PositiveInfinity));
        }

        return new Size(width, shown[0].DesiredSize.Height + Spacing + rest.Max(child => child.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown();
        if (IsOneLine)
        {
            var x = 0.0;
            foreach (var child in shown)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }

            return finalSize;
        }

        var first = shown[0].DesiredSize.Height;
        shown[0].Arrange(new Rect(0, 0, finalSize.Width, first));
        var rest = shown[1..];
        var share = Share(finalSize.Width, rest.Length);
        var height = rest.Max(child => child.DesiredSize.Height);
        for (var index = 0; index < rest.Length; index++)
        {
            rest[index].Arrange(new Rect(index * (share + Spacing), first + Spacing, share, height));
        }

        return finalSize;
    }

    private Control[] Shown() => [.. Children.Where(child => child.IsVisible)];

    private double Share(double width, int count) => Math.Max(0, (width - Spacing * (count - 1)) / count);
}

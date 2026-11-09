using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// A name and what trails it, such as a workflow's state chips. The trailing child stands on the name's line, after the name,
/// when the whole name fits beside it. Otherwise the name takes the whole width and wraps, and the trailing child goes under it.
/// </summary>
public sealed class TrailingPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<TrailingPanel, double>(nameof(Spacing), 8);

    static TrailingPanel() => AffectsMeasure<TrailingPanel>(SpacingProperty);

    /// <summary>The gap between the name and the trailing child, across or down.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The trailing child stands on the name's line.</summary>
    public bool IsInline { get; private set; }

    private (Control? Name, Control? End) Parts() =>
        (Children.Count > 0 ? Children[0] : null, Children.Count > 1 && Children[1].IsVisible ? Children[1] : null);

    protected override Size MeasureOverride(Size availableSize)
    {
        var (name, end) = Parts();
        if (name is null)
        {
            return default;
        }

        if (end is null)
        {
            name.Measure(availableSize);
            IsInline = false;
            return name.DesiredSize;
        }

        end.Measure(Size.Infinity);
        name.Measure(Size.Infinity);
        var trailing = end.DesiredSize.Width + Spacing;
        IsInline = name.DesiredSize.Width + trailing <= availableSize.Width;
        if (IsInline)
        {
            name.Measure(availableSize.WithWidth(availableSize.Width - trailing));
            return new(name.DesiredSize.Width + trailing, Math.Max(name.DesiredSize.Height, end.DesiredSize.Height));
        }

        name.Measure(availableSize);
        return new(Math.Max(name.DesiredSize.Width, end.DesiredSize.Width), name.DesiredSize.Height + Spacing / 4 + end.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (name, end) = Parts();
        if (name is null)
        {
            return finalSize;
        }

        if (end is null)
        {
            name.Arrange(new Rect(finalSize));
            return finalSize;
        }

        if (IsInline)
        {
            name.Arrange(new Rect(0, 0, name.DesiredSize.Width, finalSize.Height));
            end.Arrange(new Rect(name.DesiredSize.Width + Spacing, 0, end.DesiredSize.Width, end.DesiredSize.Height));
        }
        else
        {
            name.Arrange(new Rect(0, 0, finalSize.Width, name.DesiredSize.Height));
            end.Arrange(new Rect(0, name.DesiredSize.Height + Spacing / 4, end.DesiredSize.Width, end.DesiredSize.Height));
        }

        return finalSize;
    }
}

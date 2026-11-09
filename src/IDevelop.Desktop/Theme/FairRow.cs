using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// One line of children at their natural widths. While they do not fit, the widest give way first, so a short child keeps
/// its text and free space never sits beside a cut name. A child marked <see cref="WholeProperty"/> never gives way. The first child marked
/// <see cref="EndProperty"/> and those after it sit at the row's end.
/// </summary>
public sealed class FairRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FairRow, double>(nameof(Spacing));

    public static readonly AttachedProperty<bool> EndProperty = AvaloniaProperty.RegisterAttached<FairRow, Control, bool>("End");

    public static readonly AttachedProperty<bool> WholeProperty = AvaloniaProperty.RegisterAttached<FairRow, Control, bool>("Whole");

    static FairRow() => AffectsMeasure<FairRow>(SpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetEnd(Control control) => control.GetValue(EndProperty);

    public static void SetEnd(Control control, bool value) => control.SetValue(EndProperty, value);

    public static bool GetWhole(Control control) => control.GetValue(WholeProperty);

    public static void SetWhole(Control control, bool value) => control.SetValue(WholeProperty, value);

    /// <summary>The width that no child may pass, so that the widths sum to <paramref name="room"/>. It is infinite when they fit.</summary>
    internal static double Cap(IReadOnlyList<double> widths, double room)
    {
        if (widths.Sum() <= room)
        {
            return double.PositiveInfinity;
        }

        var left = Math.Max(0, room);
        var sorted = widths.Order().ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var share = left / (sorted.Count - index);
            if (sorted[index] >= share)
            {
                return share;
            }

            left -= sorted[index];
        }

        return double.PositiveInfinity;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Children.Where(child => child.IsVisible).ToList();
        var height = 0.0;
        foreach (var child in shown)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        }

        var gaps = Spacing * Math.Max(0, shown.Count - 1);
        var whole = shown.Where(GetWhole).Sum(child => child.DesiredSize.Width);
        var cap = Cap([.. shown.Where(child => !GetWhole(child)).Select(child => child.DesiredSize.Width)], availableSize.Width - gaps - whole);
        foreach (var child in shown)
        {
            if (double.IsFinite(cap) && !GetWhole(child))
            {
                child.Measure(new Size(Math.Min(child.DesiredSize.Width, cap), availableSize.Height));
            }

            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(shown.Sum(child => child.DesiredSize.Width) + gaps, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Children.Where(child => child.IsVisible).ToList();
        var used = shown.Sum(child => child.DesiredSize.Width) + Spacing * Math.Max(0, shown.Count - 1);
        var pushed = false;
        var x = 0.0;
        foreach (var child in shown)
        {
            if (!pushed && GetEnd(child))
            {
                pushed = true;
                x += Math.Max(0, finalSize.Width - used);
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }
}

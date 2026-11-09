using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Chrome;

/// <summary>The parts of the Command Bar, left to right.</summary>
public enum CommandBarPart
{
    /// <summary>The Index toggle.</summary>
    Toggle,

    /// <summary>The breadcrumb, which gives up its width first.</summary>
    Crumb,

    /// <summary>Undo, Redo, and Save.</summary>
    History,

    /// <summary>The Run Strip, which stands centered in the room that is left.</summary>
    Strip,

    /// <summary>Add, Generate, Merge, and Run or Stop Workflow.</summary>
    Trailing,
}

/// <summary>
/// Lays out the 44 px Command Bar on one line. The breadcrumb takes up to 360 px and the Run Strip from 320 to 480 px. The bar's own
/// controls keep their widths, and the window's caption buttons keep theirs at the bar's edge.
/// </summary>
public sealed class CommandBarPanel : Panel
{
    private const double Gap = 8;
    private const double CrumbMax = 360;
    private const double StripMin = 320;
    private const double StripMax = 480;

    /// <summary>The least room the breadcrumb keeps before the strip takes less than <see cref="StripMin"/>.</summary>
    private const double CrumbFloor = 120;

    /// <summary>The least room the strip keeps.</summary>
    private const double StripFloor = 96;

    public static readonly AttachedProperty<CommandBarPart> PartProperty = AvaloniaProperty.RegisterAttached<CommandBarPanel, Control, CommandBarPart>("Part");

    /// <summary>The width that the window's caption buttons take at the bar's left edge.</summary>
    public static readonly StyledProperty<double> ReserveLeftProperty = AvaloniaProperty.Register<CommandBarPanel, double>(nameof(ReserveLeft));

    /// <summary>The width that the window's caption buttons take at the bar's right edge.</summary>
    public static readonly StyledProperty<double> ReserveRightProperty = AvaloniaProperty.Register<CommandBarPanel, double>(nameof(ReserveRight));

    static CommandBarPanel()
    {
        AffectsParentMeasure<CommandBarPanel>(PartProperty);
        AffectsMeasure<CommandBarPanel>(ReserveLeftProperty, ReserveRightProperty);
    }

    public double ReserveLeft
    {
        get => GetValue(ReserveLeftProperty);
        set => SetValue(ReserveLeftProperty, value);
    }

    public double ReserveRight
    {
        get => GetValue(ReserveRightProperty);
        set => SetValue(ReserveRightProperty, value);
    }

    public static CommandBarPart GetPart(Control control) => control.GetValue(PartProperty);

    public static void SetPart(Control control, CommandBarPart value) => control.SetValue(PartProperty, value);

    private Control? In(CommandBarPart part) => Children.FirstOrDefault(child => child.IsVisible && GetPart(child) == part);

    /// <summary>The widths that the strip and the breadcrumb get from the room left beside the bar's fixed controls.</summary>
    public static (double Strip, double Crumb) Share(double room, double crumbNatural)
    {
        var crumb = Math.Min(crumbNatural, CrumbMax);
        var strip = Math.Clamp(room - crumb, StripMin, StripMax);
        var floor = Math.Min(crumb, CrumbFloor);
        if (room - strip < floor)
        {
            strip = Math.Max(room - floor, StripFloor);
        }

        return (strip, Math.Clamp(room - strip, 0, CrumbMax));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var (toggle, crumb, history, strip, trailing) = (In(CommandBarPart.Toggle), In(CommandBarPart.Crumb), In(CommandBarPart.History), In(CommandBarPart.Strip), In(CommandBarPart.Trailing));
        foreach (var control in new[] { toggle, history, trailing, crumb })
        {
            control?.Measure(infinite);
        }

        var fixedWidth = new[] { toggle, history, trailing }.Sum(control => control?.DesiredSize.Width ?? 0);
        var gaps = Gap * Math.Max(0, Children.Count(child => child.IsVisible) - 1);
        var room = Math.Max(0, availableSize.Width - ReserveLeft - ReserveRight - fixedWidth - gaps);
        var (stripWidth, crumbWidth) = Share(room, crumb?.DesiredSize.Width ?? 0);
        strip?.Measure(new Size(stripWidth, double.PositiveInfinity));
        crumb?.Measure(new Size(crumbWidth, double.PositiveInfinity));
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 44);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = ReserveLeft;
        void Lead(Control? control)
        {
            if (control is not null)
            {
                Center(control, left);
                left += control.DesiredSize.Width + Gap;
            }
        }

        Lead(In(CommandBarPart.Toggle));
        Lead(In(CommandBarPart.Crumb));
        Lead(In(CommandBarPart.History));
        var right = finalSize.Width - ReserveRight;
        if (In(CommandBarPart.Trailing) is { } trailing)
        {
            right -= trailing.DesiredSize.Width;
            Center(trailing, right);
            right -= Gap;
        }

        if (In(CommandBarPart.Strip) is { } strip)
        {
            Center(strip, left + Math.Max(0, (right - left - strip.DesiredSize.Width) / 2));
        }

        return finalSize;
    }

    private static void Center(Control control, double left) =>
        control.Arrange(new Rect(left, (44 - control.DesiredSize.Height) / 2, control.DesiredSize.Width, control.DesiredSize.Height));
}

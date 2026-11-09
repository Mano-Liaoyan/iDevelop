namespace IDevelop.Desktop;

/// <summary>The window's width tier, which only the window's width decides.</summary>
public enum WidthTier
{
    /// <summary>Below 1180 px. At most one of the Index and the Agent panel is wide.</summary>
    Compact,

    /// <summary>From 1180 to 1699 px. Three sheets at all times.</summary>
    Default,

    /// <summary>From 1700 to 2199 px. Three sheets, with more room for each.</summary>
    Roomy,

    /// <summary>From 2200 px. Chat is pinned beside the other pages.</summary>
    Large,
}

/// <summary>
/// How wide the Index and the Agent panel are beside the Map. A tier gives each sheet its width and its limits, and a width that
/// the person drags stays until the window enters another tier. The Map keeps <see cref="CanvasMinWidth"/> at every window size,
/// so a narrower window takes the width back from the Agent panel first, down to its minimum, and then from the Index.
/// </summary>
internal static class PanelWidths
{
    /// <summary>The Map's least width (ADR 0023).</summary>
    public const double CanvasMinWidth = 336;

    /// <summary>The Rail's width. The Rail stands flush at the window's left edge.</summary>
    public const double RailWidth = 44;

    /// <summary>The gap between two sheets and around the window's edge, which is also the resize handle.</summary>
    public const double SheetGap = 4;

    /// <summary>The Index's width while it is a flyout over the Map.</summary>
    public const double FlyoutWidth = 252;

    /// <summary>The widths that open a tier. Below the first, the tier is Compact.</summary>
    private const double DefaultFrom = 1180, RoomyFrom = 1700, LargeFrom = 2200;

    /// <summary>Below this window width the Command Bar's progress bar is shorter.</summary>
    public const double MinimalFrom = 960;

    public static WidthTier TierOf(double windowWidth) => windowWidth switch
    {
        < DefaultFrom => WidthTier.Compact,
        < RoomyFrom => WidthTier.Default,
        < LargeFrom => WidthTier.Roomy,
        _ => WidthTier.Large,
    };

    /// <summary>A sheet's width in a tier and how far the person can drag it.</summary>
    public readonly record struct SheetSize(double Width, double Min, double Max);

    /// <summary>The Index's and the Agent panel's sizes in a tier. In the Compact tier neither has a range to drag.</summary>
    public static (SheetSize Index, SheetSize Panel) SizesOf(WidthTier tier) => tier switch
    {
        WidthTier.Compact => (new(252, 252, 252), new(372, 372, 372)),
        WidthTier.Default => (new(252, 240, 400), new(400, 360, 560)),
        WidthTier.Roomy => (new(280, 240, 400), new(520, 360, 720)),
        _ => (new(300, 240, 400), new(1008, 880, 1200)),
    };

    /// <summary>The largest width each sheet may show now, as limits for its column, which the splitters also keep to.</summary>
    /// <param name="room">The window's width less the gaps and <see cref="CanvasMinWidth"/>.</param>
    /// <param name="sidebar">The Index's chosen width and its own limits.</param>
    /// <param name="inspector">The Agent panel's chosen width and its own limits.</param>
    public static (double Sidebar, double Inspector) Limits(double room, Side sidebar, Side inspector)
    {
        var (s, i) = (sidebar.Clamp(sidebar.Chosen), inspector.Clamp(inspector.Chosen));
        var over = s + i - room;
        if (over > 0)
        {
            var given = Math.Min(over, i - inspector.Min);
            i -= given;
            s -= Math.Min(over - given, s - sidebar.Min);
        }

        return (sidebar.Clamp(room - i), inspector.Clamp(room - s));
    }

    /// <summary>A sheet's chosen width and the limits it declares for itself.</summary>
    public readonly record struct Side(double Chosen, double Min, double Max)
    {
        public double Clamp(double width) => Math.Clamp(width, Min, Max);
    }
}

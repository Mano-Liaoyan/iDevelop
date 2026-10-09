namespace IDevelop.Desktop;

/// <summary>
/// How wide the sidebar and the inspector may be beside the canvas, which keeps <see cref="CanvasMinWidth"/> at every
/// window size. Each panel keeps the width the person gave it while the window has room for it. A narrower window takes the
/// width back from the inspector first, down to its minimum, and then from the sidebar, and a wider one gives it back.
/// </summary>
internal static class PanelWidths
{
    /// <summary>
    /// The canvas's least width: enough for the breadcrumb with the workflow's name, Run Workflow and Generate as glyphs, the
    /// zoom controls, and a run bar. At the 900 px minimum window it keeps both panels at their default widths.
    /// </summary>
    public const double CanvasMinWidth = 336;

    /// <summary>The largest width each panel may show now, as limits for its column, which the splitters also keep to.</summary>
    /// <param name="room">The window's width less the splitters and <see cref="CanvasMinWidth"/>.</param>
    /// <param name="sidebar">The sidebar's chosen width and its own limits.</param>
    /// <param name="inspector">The inspector's chosen width and its own limits.</param>
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

    /// <summary>A panel's chosen width and the limits it declares for itself.</summary>
    public readonly record struct Side(double Chosen, double Min, double Max)
    {
        public double Clamp(double width) => Math.Clamp(width, Min, Max);
    }
}

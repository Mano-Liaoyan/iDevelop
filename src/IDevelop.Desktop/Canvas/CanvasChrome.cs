using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The rules for the controls that float over the canvas: the breadcrumb, Run Workflow, Generate, the zoom controls,
/// the run bar, and the minimap. Each decides from the canvas's own size, so all of them switch together.
/// </summary>
internal static class CanvasChrome
{
    /// <summary>Every floating control keeps this far from the canvas's edges.</summary>
    public const double Inset = 12;

    /// <summary>The least room between two floating controls side by side.</summary>
    public const double Spacing = 12;

    /// <summary>
    /// Below this canvas width the canvas is compact: Generate and the run bar's buttons show only their glyphs, Run Workflow says Run,
    /// the breadcrumb shows only the workflow's name, and the minimap hides.
    /// </summary>
    public const double CompactWidth = 700;

    /// <summary>Below this canvas height, as with the conversation docked under it, the minimap hides.</summary>
    public const double MinimapMinHeight = 480;

    /// <summary>Below this canvas width the run bar's summary shortens to "5/9", and its tooltip keeps the whole text.</summary>
    public const double NarrowBarWidth = 520;

    /// <summary>From this many nodes a fit shows too little of each, so the minimap stays in view.</summary>
    public const int LargeGraph = 24;

    public static bool IsNarrowBar(Size canvas) => canvas.Width < NarrowBarWidth;

    public static bool IsCompact(Size canvas) => canvas.Width < CompactWidth;

    public static bool ShowsMinimap(Size canvas) => !IsCompact(canvas) && canvas.Height >= MinimapMinHeight;
}

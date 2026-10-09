using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The rules for the controls that float over the canvas: the breadcrumb, Run Workflow, Generate, the waiting pill, the
/// zoom controls, the run bars, and the minimap. Each decides from the canvas's own size, so all of them switch together.
/// </summary>
internal static class CanvasChrome
{
    /// <summary>Every floating control keeps this far from the canvas's edges.</summary>
    public const double Inset = 12;

    /// <summary>The least room between two floating controls side by side.</summary>
    public const double Spacing = 12;

    /// <summary>
    /// Below this canvas width the canvas is compact: Run Workflow, Generate, and the run bars' buttons show only their glyphs,
    /// the breadcrumb shows only the workflow's name, and the minimap hides.
    /// </summary>
    public const double CompactWidth = 640;

    /// <summary>Below this canvas height, as with the conversation docked under it, the minimap hides.</summary>
    public const double MinimapMinHeight = 480;

    public static bool IsCompact(Size canvas) => canvas.Width < CompactWidth;

    public static bool ShowsMinimap(Size canvas) => !IsCompact(canvas) && canvas.Height >= MinimapMinHeight;
}

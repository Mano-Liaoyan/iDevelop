using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IDevelop.Desktop.Canvas;

/// <summary>Centers its child on its own location, so a decorator's label stands on the midpoint of its wire at every zoom.</summary>
public sealed class WirePillAnchor : Decorator
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (Child is { } child)
        {
            child.RenderTransform = new TranslateTransform(-finalSize.Width / 2, -finalSize.Height / 2);
        }

        return arranged;
    }
}

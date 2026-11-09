using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>A zero-sized layout that centers its child on its own origin, so a decorator's child stands on the point of its location.</summary>
public sealed class WirePillAnchor : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(Size.Infinity);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var size = child.DesiredSize;
            child.Arrange(new Rect(-size.Width / 2, -size.Height / 2, size.Width, size.Height));
        }

        return default;
    }
}

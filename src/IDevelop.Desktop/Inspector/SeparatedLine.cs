using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// Two parts and the separator between them, such as a header's "Implement · Built-in, version 1": on one line while they
/// fit, else the second part under the first and the separator gone, so no line ends or starts with it. A part too long
/// for the width wraps within itself.
/// </summary>
public sealed class SeparatedLine : Panel
{
    private bool _broken;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [var first, var separator, var second])
        {
            return base.MeasureOverride(availableSize);
        }

        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }

        var (a, s, b) = (first.DesiredSize, separator.DesiredSize, second.DesiredSize);
        _broken = a.Width + s.Width + b.Width > availableSize.Width;
        return _broken
            ? new Size(Math.Max(a.Width, b.Width), a.Height + b.Height)
            : new Size(a.Width + s.Width + b.Width, Math.Max(a.Height, Math.Max(s.Height, b.Height)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is not [var first, var separator, var second])
        {
            return base.ArrangeOverride(finalSize);
        }

        if (_broken)
        {
            first.Arrange(new Rect(0, 0, finalSize.Width, first.DesiredSize.Height));
            // A separator laid out with no room draws nothing, since it clips to its bounds.
            separator.Arrange(new Rect(0, 0, 0, 0));
            second.Arrange(new Rect(0, first.DesiredSize.Height, finalSize.Width, second.DesiredSize.Height));
            return finalSize;
        }

        var x = 0.0;
        foreach (var child in new[] { first, separator, second })
        {
            child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width;
        }

        return finalSize;
    }
}

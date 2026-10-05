using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

internal sealed partial class Shell
{
    /// <summary>The card's layer that has the style class, such as its ring, stateStroke, or its tint, stateTint.</summary>
    public Border CardLayer(string title, string cssClass) =>
        Node(title).GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains(cssClass));

    public KindTile CardKind(string title) => InCard<KindTile>(title, "CardKind");

    public PathIcon CardGlyph(string title) => InCard<PathIcon>(title, "CardStateGlyph");

    /// <summary>The brush that the window's theme gives the resource key, such as KindImplementStrokeBrush.</summary>
    public Color Resource(string key) => ((ISolidColorBrush)Window.FindResource(Window.ActualThemeVariant, key)!).Color;
}

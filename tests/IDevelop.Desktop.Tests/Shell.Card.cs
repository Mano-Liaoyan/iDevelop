using Avalonia;
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

    /// <summary>
    /// The texts of a collapsed card's peek, its tooltip, which this opens as resting the pointer on the card does and then
    /// closes. Null while the card has no peek, as while it is expanded or renamed.
    /// </summary>
    public string[]? PeekTexts(string title)
    {
        var card = InCard<Panel>(title, "TaskCard");
        if (ToolTip.GetTip(card) is not Visual peek)
        {
            return null;
        }

        ToolTip.SetIsOpen(card, true);
        Render();
        var texts = Texts(peek);
        ToolTip.SetIsOpen(card, false);
        Render();
        return texts;
    }

    /// <summary>The brush that the window's theme gives the resource key, such as KindImplementStrokeBrush.</summary>
    public Color Resource(string key) => ((ISolidColorBrush)Window.FindResource(Window.ActualThemeVariant, key)!).Color;
}

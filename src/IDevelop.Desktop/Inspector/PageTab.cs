using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IDevelop.Desktop.Inspector;

/// <summary>One 52 by 48 tab of the Agent panel's strip: a glyph, a label under it, and a dot or a count on the glyph.</summary>
public sealed class PageTab : Button
{
    public static readonly StyledProperty<Geometry?> GlyphProperty = AvaloniaProperty.Register<PageTab, Geometry?>(nameof(Glyph));

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<PageTab, string?>(nameof(Label));

    public static readonly StyledProperty<string?> CountProperty = AvaloniaProperty.Register<PageTab, string?>(nameof(Count));

    protected override Type StyleKeyOverride => typeof(PageTab);

    public Geometry? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The count on the glyph, or null for none.</summary>
    public string? Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }
}

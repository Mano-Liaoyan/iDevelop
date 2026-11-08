using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// A wrapped line of groups joined by " · ", which breaks only between groups and drops the separator at a break, so no
/// line ends in a separator (ADR 0074). Each group's own words stay on one line. It styles as a <see cref="TextBlock"/>.
/// </summary>
public sealed class GroupedLine : TextBlock
{
    private const string Separator = " · ";

    public static readonly StyledProperty<string?> WholeProperty = AvaloniaProperty.Register<GroupedLine, string?>(nameof(Whole));

    static GroupedLine() => AffectsMeasure<GroupedLine>(WholeProperty);

    /// <summary>The whole line, with its groups joined by <see cref="Separator"/>. <see cref="TextBlock.Text"/> holds what shows.</summary>
    public string? Whole
    {
        get => GetValue(WholeProperty);
        set => SetValue(WholeProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Whole is { Length: > 0 } whole && double.IsFinite(availableSize.Width) ? Lines(whole, availableSize.Width - Padding.Left - Padding.Right) : Whole;
        if (Text != shown)
        {
            SetCurrentValue(TextProperty, shown);
        }

        return base.MeasureOverride(availableSize);
    }

    private string Lines(string whole, double width)
    {
        var lines = new List<string>();
        foreach (var group in whole.Split(Separator))
        {
            if (lines.Count > 0 && LineWidth(lines[^1] + Separator + group) <= width)
            {
                lines[^1] += Separator + group;
            }
            else
            {
                lines.Add(group);
            }
        }

        return string.Join('\n', lines);
    }

    private double LineWidth(string text)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, lineHeight: LineHeight);
        return layout.Width;
    }
}

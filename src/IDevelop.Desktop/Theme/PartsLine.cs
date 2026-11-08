using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// A line of parts with " · " between them that wraps only between parts, and that drops the separator at the end of a line, so a
/// wrapped line never ends in a separator. <see cref="Whole"/> holds the line, and <see cref="TextBlock.Text"/> holds what shows. It
/// styles as a <see cref="TextBlock"/>.
/// </summary>
public sealed class PartsLine : TextBlock
{
    private const string Separator = " · ";

    public static readonly StyledProperty<string?> WholeProperty = AvaloniaProperty.Register<PartsLine, string?>(nameof(Whole));

    static PartsLine() => AffectsMeasure<PartsLine>(WholeProperty);

    /// <summary>The whole line. A no-break space in a part keeps that part on one line.</summary>
    public string? Whole
    {
        get => GetValue(WholeProperty);
        set => SetValue(WholeProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>A new line shows whole at once, also while the control is hidden and not measured.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WholeProperty)
        {
            SetCurrentValue(TextProperty, Whole);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Whole is { Length: > 0 } whole && double.IsFinite(availableSize.Width)
            ? Broken(whole, availableSize.Width - Padding.Left - Padding.Right)
            : Whole;
        if (Text != shown)
        {
            SetCurrentValue(TextProperty, shown);
        }

        return base.MeasureOverride(availableSize);
    }

    private string Broken(string whole, double width)
    {
        var lines = new List<string>();
        foreach (var part in whole.Split(Separator))
        {
            if (lines.Count > 0 && WidthOf(lines[^1] + Separator + part) <= width)
            {
                lines[^1] += Separator + part;
            }
            else
            {
                lines.Add(part);
            }
        }

        return string.Join('\n', lines);
    }

    private double WidthOf(string text)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground);
        return layout.WidthIncludingTrailingWhitespace;
    }
}

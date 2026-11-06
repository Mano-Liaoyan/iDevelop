using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// Wrapped text that keeps to <see cref="TextBlock.MaxLines"/> and ends in an ellipsis when it loses text. Avalonia's own
/// trimming adds no ellipsis to text that its line limit cuts at a word's end, and it cuts a long last line short, so
/// this control finds the longest start of <see cref="Whole"/> that fits, ellipsis included. It styles as a <see cref="TextBlock"/>.
/// </summary>
public sealed class EllipsisText : TextBlock
{
    private const string Ellipsis = "…";

    public static readonly StyledProperty<string?> WholeProperty = AvaloniaProperty.Register<EllipsisText, string?>(nameof(Whole));

    static EllipsisText() => AffectsMeasure<EllipsisText>(WholeProperty);

    /// <summary>The whole text. <see cref="TextBlock.Text"/> holds what shows.</summary>
    public string? Whole
    {
        get => GetValue(WholeProperty);
        set => SetValue(WholeProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>A new text shows whole at once, also while the control is hidden and not measured.</summary>
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
        if (Whole is { Length: > 0 } whole && double.IsFinite(availableSize.Width) && MaxLines > 0)
        {
            var shown = Clamp(whole, availableSize.Width - Padding.Left - Padding.Right);
            if (Text != shown)
            {
                SetCurrentValue(TextProperty, shown);
            }
        }

        return base.MeasureOverride(availableSize);
    }

    private string Clamp(string whole, double width)
    {
        if (Fits(whole, width))
        {
            return whole;
        }

        var (fits, fails) = (0, whole.Length);
        while (fails - fits > 1)
        {
            var middle = (fits + fails) / 2;
            (fits, fails) = Fits(Cut(whole, middle), width) ? (middle, fails) : (fits, middle);
        }

        return Cut(whole, fits);
    }

    private static string Cut(string whole, int length) => whole[..length].TrimEnd() + Ellipsis;

    private bool Fits(string text, double width)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            textWrapping: TextWrapping.Wrap, maxWidth: Math.Max(1, width), lineHeight: LineHeight);
        return layout.TextLines.Count <= MaxLines;
    }
}

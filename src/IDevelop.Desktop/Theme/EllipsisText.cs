using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// Wrapped text that keeps to <see cref="TextBlock.MaxLines"/> and ends in an ellipsis when it loses text. Avalonia's own
/// trimming adds no ellipsis to text that its line limit cuts at a word's end, and it cuts a long last line short, so
/// this control finds the longest start of <see cref="Whole"/> that fits, ellipsis included. When <see cref="KeepFrom"/> marks
/// the part of the text that tells it from a similar one, the cut falls before that part, so the part stays in view. It
/// styles as a <see cref="TextBlock"/>.
/// </summary>
public sealed class EllipsisText : TextBlock
{
    private const string Ellipsis = "…";

    public static readonly StyledProperty<string?> WholeProperty = AvaloniaProperty.Register<EllipsisText, string?>(nameof(Whole));

    public static readonly StyledProperty<int> KeepFromProperty = AvaloniaProperty.Register<EllipsisText, int>(nameof(KeepFrom));

    static EllipsisText() => AffectsMeasure<EllipsisText>(WholeProperty, KeepFromProperty);

    /// <summary>The whole text. <see cref="TextBlock.Text"/> holds what shows.</summary>
    public string? Whole
    {
        get => GetValue(WholeProperty);
        set => SetValue(WholeProperty, value);
    }

    /// <summary>Where the part of <see cref="Whole"/> that must stay in view starts, or 0 when only the start matters.</summary>
    public int KeepFrom
    {
        get => GetValue(KeepFromProperty);
        set => SetValue(KeepFromProperty, value);
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

        // A plain cut will do when it leaves the word that differs in view.
        var end = Longest(whole.Length, length => Cut(whole, length), width);
        if (KeepFrom <= 0 || KeepFrom >= whole.Length || end >= WordEnd(whole, KeepFrom))
        {
            return Cut(whole, end);
        }

        var kept = whole[KeepFrom..];
        var head = Longest(KeepFrom, length => Joined(whole, length, kept), width);
        if (head > 0)
        {
            return Joined(whole, head, kept);
        }

        var tail = Ellipsis + kept;
        return Fits(tail, width) ? tail : Cut(tail, Longest(tail.Length, length => Cut(tail, length), width));
    }

    private static string Joined(string whole, int length, string kept) => whole[..length].TrimEnd() + Ellipsis + " " + kept;

    private static int WordEnd(string text, int from)
    {
        var space = text.IndexOf(' ', from);
        return space < 0 ? text.Length : space;
    }

    // The most characters, under the limit, whose text fits. No text fits at 0.
    private int Longest(int limit, Func<int, string> text, double width)
    {
        var (fits, fails) = (0, limit + 1);
        while (fails - fits > 1)
        {
            var middle = (fits + fails) / 2;
            (fits, fails) = Fits(text(middle), width) ? (middle, fails) : (fits, middle);
        }

        return fits;
    }

    private static string Cut(string whole, int length) => whole[..length].TrimEnd() + Ellipsis;

    private bool Fits(string text, double width)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            textWrapping: TextWrapping.Wrap, maxWidth: Math.Max(1, width), lineHeight: LineHeight);
        return layout.TextLines.Count <= MaxLines;
    }
}

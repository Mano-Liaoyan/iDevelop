using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// Wrapped text that keeps to <see cref="TextBlock.MaxLines"/> and ends in an ellipsis when it loses text. Avalonia's own
/// trimming adds no ellipsis to text that its line limit cuts at a word's end, and it cuts a long last line short, so
/// this control finds the longest start of <see cref="Whole"/> that fits, ellipsis included. When <see cref="KeepFrom"/> marks
/// the part of the text that tells it from a similar one and that whole part fits after an ellipsis, the text starts with the
/// ellipsis and ends whole, so the part that differs reads first. The text is cut in one place only. Under
/// <see cref="TextWrapping.WrapWithOverflow"/> no word breaks across lines. It styles as a <see cref="TextBlock"/>.
/// </summary>
public sealed class EllipsisText : TextBlock
{
    private const string Ellipsis = "…";

    public static readonly StyledProperty<string?> WholeProperty = AvaloniaProperty.Register<EllipsisText, string?>(nameof(Whole));

    public static readonly StyledProperty<int> KeepFromProperty = AvaloniaProperty.Register<EllipsisText, int>(nameof(KeepFrom));

    public static readonly DirectProperty<EllipsisText, bool> IsCutProperty =
        AvaloniaProperty.RegisterDirect<EllipsisText, bool>(nameof(IsCut), text => text.IsCut);

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

    /// <summary>The text that shows is shorter than <see cref="Whole"/>, so a More control can show only then.</summary>
    public bool IsCut
    {
        get;
        private set => SetAndRaise(IsCutProperty, ref field, value);
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

        // An unbounded measure, as a row's measure of its natural width, reads the whole text, never the cut of an earlier width.
        else if ((MaxLines == 0 || !double.IsFinite(availableSize.Width)) && Text != Whole)
        {
            SetCurrentValue(TextProperty, Whole);
        }

        var size = base.MeasureOverride(availableSize);
        IsCut = Text != Whole && !string.IsNullOrEmpty(Whole);
        // A text's natural width keeps the slack that Fits takes off, so a parent that gives it back that width sees it whole.
        return double.IsFinite(availableSize.Width) || MaxLines == 0 ? size : size.WithWidth(size.Width + Slack);
    }

    private string Clamp(string whole, double width)
    {
        if (Fits(whole, width))
        {
            return whole;
        }

        // The part that differs leads where the whole text does not fit: an ellipsis stands for the shared start, and as many of
        // its last words as fit stay before the part, so similar titles read apart at a glance ("…partition 2024-Q3", ADR 0065).
        var end = Longest(whole.Length, length => Cut(whole, length), width);
        if (KeepFrom <= 1 || KeepFrom >= whole.Length)
        {
            return Cut(whole, end);
        }

        foreach (var start in WordStarts(whole, KeepFrom))
        {
            var tail = Ellipsis + whole[start..];
            if (Fits(tail, width))
            {
                return tail;
            }
        }

        return Cut(whole, end);
    }

    // The starts of the words after the first one, up to and with the start of the kept part, the earliest first.
    private static IEnumerable<int> WordStarts(string whole, int keep)
    {
        for (var at = 1; at < keep; at++)
        {
            if (whole[at - 1] == ' ' && whole[at] != ' ')
            {
                yield return at;
            }
        }

        yield return keep;
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

    // A sentence's own period would read as a fourth dot.
    private static string Cut(string whole, int length) => whole[..length].TrimEnd().TrimEnd('.') + Ellipsis;

    // A title never breaks inside a word: a word wider than a line ends in the ellipsis.
    private bool Keeps => TextWrapping == TextWrapping.WrapWithOverflow;

    // A window at a fractional scale rounds the box to device pixels after the measure, so a text that fits only to the last
    // pixel can wrap at the arrange and lose its ellipsis past the last line. The text fits one pixel inside the box.
    private const double Slack = 1;

    private bool Fits(string text, double width)
    {
        width -= Slack;
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            textWrapping: Keeps ? TextWrapping.WrapWithOverflow : TextWrapping.Wrap, maxWidth: Math.Max(1, width), lineHeight: LineHeight);
        return layout.TextLines.Count <= MaxLines && (!Keeps || layout.TextLines.All(line => line.Width <= width));
    }
}

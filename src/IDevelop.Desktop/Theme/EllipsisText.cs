using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// Wrapped text that keeps to <see cref="TextBlock.MaxLines"/> and ends in an ellipsis when it loses text. Avalonia's own
/// trimming adds no ellipsis to text that its line limit cuts at a word's end, and it cuts a long last line short, so
/// this control finds the longest start of <see cref="Whole"/> that fits, ellipsis included. When <see cref="KeepFrom"/> marks
/// the part of the text that tells it from a similar one and that part fits after a start of the text, the cut falls before
/// that part, so the part stays in view. The text always starts with its own first word and is cut in one place only. It
/// styles as a <see cref="TextBlock"/>.
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

        else if (MaxLines == 0 && Text != Whole)
        {
            SetCurrentValue(TextProperty, Whole);
        }

        var size = base.MeasureOverride(availableSize);
        IsCut = Text != Whole && !string.IsNullOrEmpty(Whole);
        return size;
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
        return head > 0 ? Joined(whole, head, kept) : Cut(whole, end);
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

    // A sentence's own period would read as a fourth dot.
    private static string Cut(string whole, int length) => whole[..length].TrimEnd().TrimEnd('.') + Ellipsis;

    private bool Fits(string text, double width)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            textWrapping: TextWrapping.Wrap, maxWidth: Math.Max(1, width), lineHeight: LineHeight);
        return layout.TextLines.Count <= MaxLines;
    }
}

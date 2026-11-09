using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// Wrapped text that keeps to <see cref="TextBlock.MaxLines"/> and ends in an ellipsis when it loses text. Avalonia's own
/// trimming adds no ellipsis to text that its line limit cuts at a word's end, and it cuts a long last line short, so
/// this control finds the longest start of <see cref="Whole"/> that fits, ellipsis included. When <see cref="Key"/> marks
/// the words that tell the text from every other title, a cut text keeps them: a start that holds the first words that differ,
/// else an ellipsis and an end that holds the last words that differ, else the inner words that differ between two ellipses
/// (ADR 0069). Under
/// <see cref="TextWrapping.WrapWithOverflow"/> no word breaks across lines. It styles as a <see cref="TextBlock"/>.
/// </summary>
public sealed class EllipsisText : TextBlock
{
    private const string Ellipsis = "…";

    public static readonly StyledProperty<string?> WholeProperty = AvaloniaProperty.Register<EllipsisText, string?>(nameof(Whole));

    public static readonly StyledProperty<TitleKey?> KeyProperty = AvaloniaProperty.Register<EllipsisText, TitleKey?>(nameof(Key));

    public static readonly DirectProperty<EllipsisText, bool> IsCutProperty =
        AvaloniaProperty.RegisterDirect<EllipsisText, bool>(nameof(IsCut), text => text.IsCut);

    static EllipsisText() => AffectsMeasure<EllipsisText>(WholeProperty, KeyProperty, EndWordsProperty);

    /// <summary>The whole text. <see cref="TextBlock.Text"/> holds what shows.</summary>
    public string? Whole
    {
        get => GetValue(WholeProperty);
        set => SetValue(WholeProperty, value);
    }

    /// <summary>The words of <see cref="Whole"/> that tell it from the other titles, or null when only the start matters.</summary>
    public TitleKey? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>
    /// How many last words a cut keeps after the ellipsis, as a model name's last word, which tells "GPT-6.1 Sol" from "GPT-6.1
    /// Terra": a cut reads "GPT-6.1… Terra" (ADR 0069). 0 cuts at the end. <see cref="Key"/> comes first where it is set.
    /// </summary>
    public int EndWords
    {
        get => GetValue(EndWordsProperty);
        set => SetValue(EndWordsProperty, value);
    }

    public static readonly StyledProperty<int> EndWordsProperty = AvaloniaProperty.Register<EllipsisText, int>(nameof(EndWords));

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

        var end = Longest(whole.Length, length => Cut(whole, length), width);
        if (Key is not { } key)
        {
            return EndWords > 0 ? KeepEnd(whole, width) ?? Cut(whole, end) : Cut(whole, end);
        }

        var starts = TitleKey.WordStarts(whole);
        var count = starts.Length;

        // A start that holds the first words that differ reads apart, and it keeps the title's own start.
        if (key.Head is > 0 and var head && head <= count && end >= TitleKey.WordEnd(whole, starts[head - 1]))
        {
            return Cut(whole, end);
        }

        // Else the title's first word and the last words that differ show around an ellipsis, with as many words before them as fit, so
        // similar titles read apart and each still says what it does ("Create… partition 2024-Q3", ADR 0074). A line too short for the first word
        // keeps only what differs ("…partition 2024-Q3", ADR 0065).
        if (key.Tail is > 0 and var tail && tail < count)
        {
            var from = starts[count - tail];
            var verb = whole[..TitleKey.WordEnd(whole, 0)] + Ellipsis + " ";
            foreach (var start in starts.Skip(1).Where(start => start <= from && Fits(verb + whole[start..], width)))
            {
                return verb + whole[start..];
            }

            foreach (var start in starts.Skip(1).Where(start => start <= from))
            {
                if (Fits(Ellipsis + whole[start..], width))
                {
                    return Ellipsis + whole[start..];
                }
            }

            // On the smallest cards a kept token such as 2024-Q3 can be wider than a line. It then breaks at its hyphen, so the
            // part that differs still leads (ADR 0067).
            var loose = Ellipsis + NameBreaks.Loose(whole[from..]);
            if (loose != Ellipsis + whole[from..] && Fits(loose, width))
            {
                return loose;
            }
        }

        // Else the inner words that differ show between two ellipses, with as many words before them as fit.
        if (key.MiddleFrom > 0 && key.MiddleTo > key.MiddleFrom && key.MiddleTo < count)
        {
            var stop = TitleKey.WordEnd(whole, starts[key.MiddleTo - 1]);
            foreach (var start in starts.Skip(1).Take(key.MiddleFrom))
            {
                var middle = Ellipsis + whole[start..stop] + Ellipsis;
                if (Fits(middle, width))
                {
                    return middle;
                }
            }
        }

        return Cut(whole, end);
    }

    // A start shorter than this reads as no name at all, as "G… Terra" does, so the text ends in the ellipsis instead.
    private const int LeastHead = 3;

    // The longest start of the text before its last EndWords words that fits with them after an ellipsis, or null.
    private string? KeepEnd(string whole, double width)
    {
        var starts = TitleKey.WordStarts(whole);
        if (starts.Length <= EndWords)
        {
            return null;
        }

        var from = starts[^EndWords];
        var tail = whole[from..];
        var head = whole[..from].TrimEnd();
        var fits = Longest(head.Length, length => Cut(head, length) + " " + tail, width);
        return fits >= Math.Min(LeastHead, head.Length) && fits > 0 ? Cut(head, fits) + " " + tail : null;
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

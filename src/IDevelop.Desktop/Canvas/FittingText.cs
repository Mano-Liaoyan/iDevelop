using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A one-line text that shows the first of its <see cref="Choices"/> that fits its width whole, and the last one when none
/// does, so a card can name what it waits for when the name fits and count it when it does not (#90). It styles as a
/// <see cref="TextBlock"/>.
/// </summary>
public sealed class FittingText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ChoicesProperty =
        AvaloniaProperty.Register<FittingText, IReadOnlyList<string>?>(nameof(Choices));

    static FittingText() => AffectsMeasure<FittingText>(ChoicesProperty);

    /// <summary>The texts to choose from, the fullest first.</summary>
    public IReadOnlyList<string>? Choices
    {
        get => GetValue(ChoicesProperty);
        set => SetValue(ChoicesProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>New choices show the fullest at once, also while the text is hidden and not measured.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChoicesProperty)
        {
            SetCurrentValue(TextProperty, Choices is { Count: > 0 } choices ? choices[0] : null);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Choices is { Count: > 0 } choices)
        {
            var chosen = choices[^1];
            foreach (var choice in choices)
            {
                if (!double.IsFinite(availableSize.Width) || WidthOf(choice) <= availableSize.Width)
                {
                    chosen = choice;
                    break;
                }
            }

            if (Text != chosen)
            {
                SetCurrentValue(TextProperty, chosen);
            }
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>The width <paramref name="text"/> takes on one line in this control's font, with its padding.</summary>
    private double WidthOf(string text)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground);
        return layout.WidthIncludingTrailingWhitespace + Padding.Left + Padding.Right;
    }
}

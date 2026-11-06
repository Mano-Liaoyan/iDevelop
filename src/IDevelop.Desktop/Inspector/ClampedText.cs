using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Inspector;

/// <summary>Wrapped text cut to <see cref="TextBlock.MaxLines"/> that says whether it lost lines, so a More button can show only then.</summary>
public sealed class ClampedText : TextBlock
{
    public static readonly DirectProperty<ClampedText, bool> IsClampedProperty =
        AvaloniaProperty.RegisterDirect<ClampedText, bool>(nameof(IsClamped), text => text.IsClamped);

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public bool IsClamped
    {
        get;
        private set => SetAndRaise(IsClampedProperty, ref field, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var whole = new TextLayout(Text, new Avalonia.Media.Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground,
            textWrapping: TextWrapping, maxWidth: double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width, lineHeight: LineHeight);
        IsClamped = whole.TextLines.Count > MaxLines && MaxLines > 0;
        return size;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The one pattern for long text in the inspector (ADR 0045): its child shows whole when it fits in <see cref="Rest"/>, and
/// otherwise rests at that height with a More control under it, which <see cref="Folds"/> shows and <see cref="IsOpen"/> follows.
/// The focus opens it too, so a person never edits text out of view.
/// </summary>
public sealed class FoldPanel : Decorator
{
    public static readonly StyledProperty<double> RestProperty = AvaloniaProperty.Register<FoldPanel, double>(nameof(Rest), 122);

    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<FoldPanel, bool>(nameof(IsOpen));

    public static readonly DirectProperty<FoldPanel, bool> FoldsProperty =
        AvaloniaProperty.RegisterDirect<FoldPanel, bool>(nameof(Folds), panel => panel.Folds);

    public static readonly DirectProperty<FoldPanel, string> MoreLabelProperty =
        AvaloniaProperty.RegisterDirect<FoldPanel, string>(nameof(MoreLabel), panel => panel.MoreLabel);

    private bool _hasFocus;

    static FoldPanel()
    {
        AffectsMeasure<FoldPanel>(RestProperty, IsOpenProperty);
        ClipToBoundsProperty.OverrideDefaultValue<FoldPanel>(true);
    }

    /// <summary>The height the text rests at while it folds.</summary>
    public double Rest
    {
        get => GetValue(RestProperty);
        set => SetValue(RestProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>The text is taller than <see cref="Rest"/>, so the More control shows.</summary>
    public bool Folds
    {
        get;
        private set => SetAndRaise(FoldsProperty, ref field, value);
    }

    /// <summary>What the More control says.</summary>
    public string MoreLabel => IsOpen ? "Less" : "More";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            RaisePropertyChanged(MoreLabelProperty, change.GetOldValue<bool>() ? "Less" : "More", MoreLabel);
        }
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        InvalidateMeasure();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        child.Measure(availableSize.WithHeight(double.PositiveInfinity));
        var whole = child.DesiredSize.Height;
        Folds = whole > Rest + 1;
        return child.DesiredSize.WithHeight(Folds && !IsOpen && !_hasFocus ? RestHeight(child) : whole);
    }

    // A stack of blocks, such as a prompt's paragraphs, rests on a whole line, so no line shows cut in half.
    private double RestHeight(Control child)
    {
        if (child is not StackPanel stack)
        {
            return Rest;
        }

        var height = 0.0;
        foreach (var block in stack.Children)
        {
            var next = height + block.DesiredSize.Height + (height > 0 ? stack.Spacing : 0);
            if (next > Rest)
            {
                var lines = block is TextBlock { LineHeight: > 0 } text ? Math.Floor((Rest - height - stack.Spacing) / text.LineHeight) * text.LineHeight : 0;
                return height + (lines > 0 ? stack.Spacing + lines : 0);
            }

            height = next;
        }

        return height;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

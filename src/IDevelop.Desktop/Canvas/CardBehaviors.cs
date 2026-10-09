using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// What a card's template needs beyond its bindings (#94, ADR 0024): it tells its node the size it shows, so the canvas lays
/// the cards out and routes the wires around that size, and a collapsed card's tooltip is its peek.
/// </summary>
public static class CardBehaviors
{
    /// <summary>The state glyph sits on the title's line: centred 16 px down, where the 18 px title line, 7 px down, centres.</summary>
    public static readonly Thickness GlyphMargin = new(0, 8, 0, 0);

    /// <summary>The disclosure's 28 px target centres on the last line, 49 px down, and overhangs its 16 px column by 6 px each side.</summary>
    public static readonly Thickness ChevronMargin = new(-6, 35, -6, 0);

    /// <summary>A port's 20 px square centres on the header's middle line, 32 px down.</summary>
    public static readonly Thickness PortMargin = new(0, WorkflowCanvasViewModel.InputPortCenter.Y - 10, 0, 0);

    public static readonly AttachedProperty<bool> ReportsSizeProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("ReportsSize", typeof(CardBehaviors));

    public static readonly AttachedProperty<bool> PeekProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Peek", typeof(CardBehaviors));

    static CardBehaviors()
    {
        ReportsSizeProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
            {
                control.SizeChanged += (_, args) =>
                {
                    if (control.DataContext is TaskNodeViewModel node)
                    {
                        node.CardSize = args.NewSize;
                    }
                };
            }
        });
        // The peek is made once for each card, follows the card's node, and comes back whenever the card collapses again.
        PeekProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true)
            {
                ToolTip.SetTip(control, null);
                return;
            }

            if (control.GetValue(PeekControlProperty) is not { } peek)
            {
                peek = new CardPeek();
                peek.Bind(StyledElement.DataContextProperty, control.GetObservable(StyledElement.DataContextProperty));
                control.SetValue(PeekControlProperty, peek);
            }

            ToolTip.SetTip(control, peek);
        });
    }

    private static readonly AttachedProperty<CardPeek?> PeekControlProperty =
        AvaloniaProperty.RegisterAttached<Control, CardPeek?>("PeekControl", typeof(CardBehaviors));

    public static bool GetReportsSize(Control control) => control.GetValue(ReportsSizeProperty);

    public static void SetReportsSize(Control control, bool value) => control.SetValue(ReportsSizeProperty, value);

    public static bool GetPeek(Control control) => control.GetValue(PeekProperty);

    public static void SetPeek(Control control, bool value) => control.SetValue(PeekProperty, value);
}

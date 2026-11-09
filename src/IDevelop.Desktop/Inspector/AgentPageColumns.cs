using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// Lays out the Chat page, the rule, and the other pages. The pages share one area, so only the current one shows. Pinned, Chat
/// takes the width that the page column leaves, and a 1 px rule stands between them. The first child is Chat and the second is the rule.
/// </summary>
public sealed class AgentPageColumns : Panel
{
    private const double RuleWidth = 1;

    public static readonly StyledProperty<bool> IsPinnedProperty = AvaloniaProperty.Register<AgentPageColumns, bool>(nameof(IsPinned));

    public static readonly StyledProperty<bool> IsRoomProperty = AvaloniaProperty.Register<AgentPageColumns, bool>(nameof(IsRoom));

    static AgentPageColumns() => AffectsMeasure<AgentPageColumns>(IsPinnedProperty, IsRoomProperty);

    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    public bool IsRoom
    {
        get => GetValue(IsRoomProperty);
        set => SetValue(IsRoomProperty, value);
    }

    private (double Chat, double Page) Columns(double width)
    {
        if (!IsPinned)
        {
            return (width, width);
        }

        var page = PanelWidths.PageColumnWidth(width, IsRoom);
        return (Math.Max(0, width - page - RuleWidth), page);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (chat, page) = Columns(availableSize.Width);
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Measure(availableSize.WithWidth(index switch { 0 => chat, 1 => RuleWidth, _ => page }));
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (chat, page) = Columns(finalSize.Width);
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(index switch
            {
                0 => new Rect(0, 0, chat, finalSize.Height),
                1 => new Rect(chat, 0, RuleWidth, finalSize.Height),
                _ => new Rect(finalSize.Width - page, 0, page, finalSize.Height),
            });
        }

        return finalSize;
    }
}

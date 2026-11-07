using Avalonia;
using Avalonia.Controls.Primitives;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Theme;

/// <summary>A state's one badge in a list: the disc and the bold glyph that the state's cards show, at the size the list sets.</summary>
public sealed class StateBadge : TemplatedControl
{
    public static readonly StyledProperty<NodeState?> StateProperty = AvaloniaProperty.Register<StateBadge, NodeState?>(nameof(State));

    public static readonly StyledProperty<WaitReason?> WhyProperty = AvaloniaProperty.Register<StateBadge, WaitReason?>(nameof(Why));

    public NodeState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public WaitReason? Why
    {
        get => GetValue(WhyProperty);
        set => SetValue(WhyProperty, value);
    }
}

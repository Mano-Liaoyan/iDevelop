using Avalonia;
using Avalonia.Controls.Primitives;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// The one glyph that shows a node's state on every surface: a hollow ring, a rotating arc, or a filled disc with a glyph.
/// A node that waits for the person keeps the orange disc for every reason, and the disc's glyph tells the reason. A role adds
/// a 6 px accent dot and no state word. It is 14 px in rows and 16 px on cards and in headers.
/// </summary>
public sealed class StateMark : TemplatedControl
{
    public static readonly StyledProperty<NodeState?> StateProperty = AvaloniaProperty.Register<StateMark, NodeState?>(nameof(State));

    public static readonly StyledProperty<WaitReason?> WhyProperty = AvaloniaProperty.Register<StateMark, WaitReason?>(nameof(Why));

    public static readonly StyledProperty<NodeRole?> RoleProperty = AvaloniaProperty.Register<StateMark, NodeRole?>(nameof(Role));

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

    public NodeRole? Role
    {
        get => GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }
}

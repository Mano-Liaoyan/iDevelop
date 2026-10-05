using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// Attached properties that give an element one style class for a node's kind, state, or role, such as kind-plan,
/// state-failed, or role-proposing, and take away the class of the value before. Kinds.axaml paints by these classes,
/// so XAML never names a hue.
/// </summary>
public sealed class StyleClass
{
    public static readonly AttachedProperty<NodeKind?> KindProperty = AvaloniaProperty.RegisterAttached<StyleClass, StyledElement, NodeKind?>("Kind");

    public static readonly AttachedProperty<NodeState?> StateProperty = AvaloniaProperty.RegisterAttached<StyleClass, StyledElement, NodeState?>("State");

    public static readonly AttachedProperty<NodeRole?> RoleProperty = AvaloniaProperty.RegisterAttached<StyleClass, StyledElement, NodeRole?>("Role");

    static StyleClass()
    {
        Swap(KindProperty, kind => NodeKinds.Info(kind).StyleClass);
        Swap(StateProperty, state => $"state-{state.ToString().ToLowerInvariant()}");
        Swap(RoleProperty, role => $"role-{role.ToString().ToLowerInvariant()}");
    }

    private StyleClass()
    {
    }

    public static NodeKind? GetKind(StyledElement element) => element.GetValue(KindProperty);

    public static void SetKind(StyledElement element, NodeKind? value) => element.SetValue(KindProperty, value);

    public static NodeState? GetState(StyledElement element) => element.GetValue(StateProperty);

    public static void SetState(StyledElement element, NodeState? value) => element.SetValue(StateProperty, value);

    public static NodeRole? GetRole(StyledElement element) => element.GetValue(RoleProperty);

    public static void SetRole(StyledElement element, NodeRole? value) => element.SetValue(RoleProperty, value);

    private static void Swap<T>(AttachedProperty<T?> property, Func<T, string> className) where T : struct, Enum =>
        property.Changed.AddClassHandler<StyledElement, T?>((element, change) =>
        {
            if (change.OldValue.GetValueOrDefault() is { } old)
            {
                element.Classes.Remove(className(old));
            }

            if (change.NewValue.GetValueOrDefault() is { } value)
            {
                element.Classes.Add(className(value));
            }
        });
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Draws a context connection under the dependencies. Where the two share a run from one output, the solid line on top
/// is the one a click or a right-click reaches. Nodify wraps each connection in a container of its own, so the layer is
/// the container's.
/// </summary>
public static class WireLayer
{
    public static readonly AttachedProperty<bool> IsBelowProperty = AvaloniaProperty.RegisterAttached<Control, bool>("IsBelow", typeof(WireLayer));

    static WireLayer()
    {
        IsBelowProperty.Changed.AddClassHandler<Control>((wire, _) =>
        {
            wire.AttachedToVisualTree -= OnAttached;
            wire.AttachedToVisualTree += OnAttached;
            Apply(wire);
        });
    }

    public static bool GetIsBelow(Control wire) => wire.GetValue(IsBelowProperty);

    public static void SetIsBelow(Control wire, bool value) => wire.SetValue(IsBelowProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Apply((Control)sender!);

    private static void Apply(Control wire)
    {
        if (wire.GetVisualParent() is Control container)
        {
            container.ZIndex = GetIsBelow(wire) ? -1 : 0;
        }
    }
}

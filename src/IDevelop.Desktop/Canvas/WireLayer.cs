using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Sets the layer of a wire. A context connection draws under the dependencies, so where the two share a run the solid line
/// is the one a click or a right-click reaches. A wire that feeds a running node draws over the rest, so the active path stays
/// the loudest wire where wires cross. Nodify wraps each connection in a container of its own, so the layer is the container's.
/// </summary>
public static class WireLayer
{
    public static readonly AttachedProperty<int> LevelProperty = AvaloniaProperty.RegisterAttached<Control, int>("Level", typeof(WireLayer));

    static WireLayer()
    {
        LevelProperty.Changed.AddClassHandler<Control>((wire, _) =>
        {
            wire.AttachedToVisualTree -= OnAttached;
            wire.AttachedToVisualTree += OnAttached;
            Apply(wire);
        });
    }

    public static int GetLevel(Control wire) => wire.GetValue(LevelProperty);

    public static void SetLevel(Control wire, int value) => wire.SetValue(LevelProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Apply((Control)sender!);

    private static void Apply(Control wire)
    {
        if (wire.GetVisualParent() is Control container)
        {
            container.ZIndex = GetLevel(wire);
        }
    }
}

using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Theme;

/// <summary>Marks a scroll viewer with more content below its bottom edge, so that its style fades that edge and no row ends cut in half.</summary>
public static class EdgeFade
{
    public static readonly AttachedProperty<bool> IsEnabledProperty = AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(EdgeFade));

    static EdgeFade() => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, change) =>
    {
        if (change.NewValue is true)
        {
            viewer.Classes.Add("fadesEnd");
            viewer.ScrollChanged += (_, _) => viewer.Classes.Set("more", viewer.Offset.Y < viewer.ScrollBarMaximum.Y - 1);
        }
    });

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);
}

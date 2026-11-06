using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IDevelop.Desktop.Theme;

/// <summary>Marks a scroll viewer with more content below its bottom edge, so that its style fades that edge and no row ends cut in half.</summary>
public static class EdgeFade
{
    /// <summary>The mask that fades the top 36 px of a view. It holds opacity, not a color.</summary>
    public static readonly IBrush TopFade = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(0, 36, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(Colors.Transparent, 0.2), new GradientStop(Colors.Black, 1) },
    };

    /// <summary>The mask that fades the last tenth of a view.</summary>
    public static readonly IBrush EndFade = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.9, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) },
    };

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

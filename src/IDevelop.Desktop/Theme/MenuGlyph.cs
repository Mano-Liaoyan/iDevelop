using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// An attached property that gives a menu item a glyph icon from one geometry. XAML sets <c>theme:MenuGlyph.Data</c> in
/// place of a <see cref="MenuItem.Icon"/> block that holds a <see cref="PathIcon"/> in the Glyph theme.
/// </summary>
public static class MenuGlyph
{
    public static readonly AttachedProperty<Geometry?> DataProperty = AvaloniaProperty.RegisterAttached<MenuItem, Geometry?>("Data", typeof(MenuGlyph));

    static MenuGlyph() =>
        DataProperty.Changed.AddClassHandler<MenuItem, Geometry?>((item, change) =>
            item.Icon = new PathIcon { Theme = (ControlTheme?)Application.Current!.FindResource("Glyph"), Data = change.NewValue.GetValueOrDefault() });

    public static Geometry? GetData(MenuItem item) => item.GetValue(DataProperty);

    public static void SetData(MenuItem item, Geometry? value) => item.SetValue(DataProperty, value);
}

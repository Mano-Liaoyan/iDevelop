using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The canvas shortcuts that menus and the Add popover show as hints. "Ctrl" is the platform's command modifier, so
/// Cmd on macOS.
/// </summary>
public static class CanvasKeys
{
    public static KeyModifiers Command => Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    public static KeyGesture Run => new(Key.Enter, Command);

    public static KeyGesture Rename { get; } = new(Key.F2);

    public static KeyGesture Duplicate => new(Key.D, Command);

    public static KeyGesture SelectAll => new(Key.A, Command);

    public static KeyGesture Delete { get; } = new(Key.Delete);

    public static KeyGesture Fit { get; } = new(Key.F);

    public static KeyGesture ZoomToActual { get; } = new(Key.D0);

    public static KeyGesture Add { get; } = new(Key.N);

    /// <summary>
    /// The gesture as the platform writes it, such as Ctrl+D or ⌘D. Outside macOS, Avalonia names Enter "Return" and
    /// Delete "Delete", so the hint uses Enter and Del, the names printed on those keys.
    /// </summary>
    public static string Hint(KeyGesture gesture)
    {
        var text = gesture.ToString("p", null);
        var key = KeyGestureFormatInfo.GetInstance(null).FormatKey(gesture.Key);
        return ShortKeyNames.TryGetValue(key, out var name) ? text[..^key.Length] + name : text;
    }

    /// <summary>Writes a menu item's <see cref="MenuItem.InputGesture"/> as <see cref="Hint"/> does.</summary>
    public static IValueConverter HintConverter { get; } = new FuncValueConverter<KeyGesture?, string?>(gesture => gesture is null ? null : Hint(gesture));

    private static readonly Dictionary<string, string> ShortKeyNames = new() { ["Return"] = "Enter", ["Delete"] = "Del" };
}

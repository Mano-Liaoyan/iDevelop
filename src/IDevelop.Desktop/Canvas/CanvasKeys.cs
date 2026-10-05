using Avalonia;
using Avalonia.Input;

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

    /// <summary>The gesture as the platform writes it, such as Ctrl+D or ⌘D.</summary>
    public static string Hint(KeyGesture gesture) => gesture.ToString("p", null);
}

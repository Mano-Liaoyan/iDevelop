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

    /// <summary>Runs the shown workflow, or Stop and send in a composer.</summary>
    public static KeyGesture RunWorkflow => new(Key.Enter, Command | KeyModifiers.Shift);

    /// <summary>Puts the focus in the left area's Find box.</summary>
    public static KeyGesture Find => new(Key.K, Command);

    /// <summary>Selects the next node that waits for the person or failed.</summary>
    public static KeyGesture NextAttention => new(Key.J, Command);

    /// <summary>Shows the next node that failed. A failed node does not wait, so it has its own key.</summary>
    public static KeyGesture NextFailed => new(Key.J, Command | KeyModifiers.Shift);

    /// <summary>The Model row's tooltip.</summary>
    public static string ModelHint => $"Model ({Hint(ModelPicker)})";

    /// <summary>Shows or hides the left panel.</summary>
    public static KeyGesture ToggleSidebar => new(Key.B, Command);

    /// <summary>Shows or hides the inspector.</summary>
    public static KeyGesture ToggleInspector => new(Key.B, Command | KeyModifiers.Alt);

    public static KeyGesture ModelPicker { get; } = new(Key.M, KeyModifiers.Alt);

    /// <summary>Also puts the focus in Find.</summary>
    public static KeyGesture FindAlso => new(Key.F, Command);

    /// <summary>The Agent panel's page for Alt+1 to Alt+6, or null for any other key.</summary>
    public static Inspector.AgentPage? Page(KeyEventArgs e) => e.KeyModifiers == KeyModifiers.Alt && e.Key is >= Key.D1 and <= Key.D6
        ? (Inspector.AgentPage)(e.Key - Key.D1)
        : null;

    public static KeyGesture Rename { get; } = new(Key.F2);

    public static KeyGesture Duplicate => new(Key.D, Command);

    public static KeyGesture SelectAll => new(Key.A, Command);

    public static KeyGesture Delete { get; } = new(Key.Delete);

    public static KeyGesture Fit { get; } = new(Key.F);

    public static KeyGesture FrameSelection { get; } = new(Key.OemPeriod);

    public static KeyGesture FrameRunning { get; } = new(Key.OemPeriod, KeyModifiers.Shift);

    public static KeyGesture ZoomToActual { get; } = new(Key.D0);

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

using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// What the person folded in one window's Agent panel, by fold panel key. It does not belong to a node, so it survives a change
/// of selection. Panels and rows read it from the inherited <see cref="CurrentProperty"/>.
/// </summary>
public sealed class InspectorState
{
    public static readonly AttachedProperty<InspectorState?> CurrentProperty =
        AvaloniaProperty.RegisterAttached<InspectorState, Control, InspectorState?>("Current", inherits: true);

    private readonly Dictionary<string, bool> _folded = [];

    public event EventHandler? Changed;

    public static InspectorState? GetCurrent(Control control) => control.GetValue(CurrentProperty);

    public static void SetCurrent(Control control, InspectorState? value) => control.SetValue(CurrentProperty, value);

    /// <summary>Whether the panel is folded: the person's choice, or else <paramref name="startsFolded"/>.</summary>
    public bool IsFolded(string key, bool startsFolded = false) => _folded.TryGetValue(key, out var folded) ? folded : startsFolded;

    public void Fold(string key, bool folded)
    {
        if (!_folded.TryGetValue(key, out var now) || now != folded)
        {
            _folded[key] = folded;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

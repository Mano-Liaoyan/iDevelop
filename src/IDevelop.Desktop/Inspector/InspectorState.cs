using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// What the person set in one window's inspector: the filter text and the folded sections, by section key. Neither
/// belongs to a node, so both survive a change of selection. Sections and rows read it from the inherited
/// <see cref="CurrentProperty"/>.
/// </summary>
public sealed class InspectorState
{
    public static readonly AttachedProperty<InspectorState?> CurrentProperty =
        AvaloniaProperty.RegisterAttached<InspectorState, Control, InspectorState?>("Current", inherits: true);

    private readonly HashSet<string> _folded = [];

    public event EventHandler? Changed;

    public string Query { get; private set; } = "";

    public bool IsFiltering => !string.IsNullOrWhiteSpace(Query);

    public static InspectorState? GetCurrent(Control control) => control.GetValue(CurrentProperty);

    public static void SetCurrent(Control control, InspectorState? value) => control.SetValue(CurrentProperty, value);

    public bool IsFolded(string key) => _folded.Contains(key);

    public void Filter(string? query)
    {
        if ((query ?? "") != Query)
        {
            Query = query ?? "";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UnfoldAll() => Fold([.. _folded], folded: false);

    public void Fold(IEnumerable<string> keys, bool folded)
    {
        var changed = false;
        foreach (var key in keys)
        {
            changed |= folded ? _folded.Add(key) : _folded.Remove(key);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

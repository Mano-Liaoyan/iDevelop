using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// One field of a task's blueprint in the inspector. A node's blueprint never changes, so each task keeps the same
/// instances, and a box keeps its focus while each keystroke round-trips through the workflow.
/// </summary>
public sealed class FieldViewModel : ObservableObject
{
    private readonly TaskNodeViewModel _node;

    internal FieldViewModel(TaskNodeViewModel node, FieldSpec spec)
    {
        _node = node;
        Spec = spec;
    }

    public string Label => Spec.Label;

    public bool IsMultiline => Spec.Shape == FieldShape.Text;

    /// <summary>"Task" and the key with its first letter raised, such as TaskInstructions, the ids the inspector's boxes always had.</summary>
    public string AutomationId => $"Task{char.ToUpperInvariant(Spec.Key[0])}{Spec.Key[1..]}";

    public string Text
    {
        get => _node.FieldText(Spec.Key);
        set => _node.SetField(Spec.Key, value);
    }

    internal FieldSpec Spec { get; }

    internal void Refresh() => OnPropertyChanged(nameof(Text));
}

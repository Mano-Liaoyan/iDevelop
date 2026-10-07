using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// One field of a task's blueprint in the inspector. A node's blueprint never changes, so each task keeps the same
/// instances, and a box keeps its focus while each keystroke round-trips through the workflow.
/// </summary>
public sealed class FieldViewModel : ObservableObject
{
    /// <summary>
    /// The icon of each built-in field key, which a derived blueprint keeps with its meaning. Any other key is a field a
    /// person defined, whose meaning the app cannot know, so it shows the neutral field icon.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>
    {
        ["instructions"] = "IconEdit",
        ["acceptanceCriteria"] = "IconCheckmark",
        ["goal"] = "IconGoal",
        ["constraints"] = "IconConstraints",
        ["brief"] = "IconBrief",
        ["focus"] = "IconChecklist",
        ["checklist"] = "IconChecklist",
    };

    private readonly TaskNodeViewModel _node;

    internal FieldViewModel(TaskNodeViewModel node, FieldSpec spec)
    {
        _node = node;
        Spec = spec;
        RevertCommand = new RelayCommand(() => _node.SetField(Spec.Key, Spec.Default));
    }

    public string Label => Spec.Label;

    /// <summary>The label's tooltip, which says when the field is required.</summary>
    public string LabelTip => Spec.Required ? $"{Spec.Label} · Required" : Spec.Label;

    public string IconKey => Icons.GetValueOrDefault(Spec.Key, "IconField");

    public bool IsMultiline => Spec.Shape == FieldShape.Text;

    /// <summary>"Task" and the key with its first letter raised, such as TaskInstructions, the ids the inspector's boxes always had.</summary>
    public string AutomationId => $"Task{KeyName}";

    /// <summary>The id of the field's revert arrow, such as RevertInstructions.</summary>
    public string RevertId => $"Revert{KeyName}";

    public string Text
    {
        get => _node.FieldText(Spec.Key);
        set => _node.SetField(Spec.Key, value);
    }

    /// <summary>The text differs from the blueprint's default for the field.</summary>
    public bool CanRevert => Text != Spec.Default;

    /// <summary>Puts the blueprint's default back in one edit.</summary>
    public ICommand RevertCommand { get; }

    internal FieldSpec Spec { get; }

    private string KeyName => $"{char.ToUpperInvariant(Spec.Key[0])}{Spec.Key[1..]}";

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(CanRevert));
    }
}

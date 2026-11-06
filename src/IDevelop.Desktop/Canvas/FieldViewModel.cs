using System.Windows.Input;
using IDevelop.Desktop.Execution;
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
        RevertCommand = new RelayCommand(() => _node.SetField(Spec.Key, Spec.Default));
    }

    public string Label => Spec.Label;

    /// <summary>What an empty box says, such as "Add acceptance criteria".</summary>
    public string Placeholder => $"Add {Spec.Label.ToLowerInvariant()}";

    /// <summary>The label's tooltip, which says when the field is required.</summary>
    public string LabelTip => Spec.Required ? $"{Spec.Label} · Required" : Spec.Label;

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

    /// <summary>The text as it reads at rest: its Markdown marks gone, list items as bullets, and no blank lines. The box edits the source.</summary>
    public string Reading => ResultViewModel.Reading(Text);

    /// <summary>The id of the reading, such as TaskInstructionsReading.</summary>
    public string ReadingId => $"{AutomationId}Reading";

    /// <summary>The id of the More control under the reading, such as TaskInstructionsMore.</summary>
    public string ReadingMoreId => $"{AutomationId}More";

    /// <summary>The person opened the whole reading. It stays open while the node keeps the field.</summary>
    public bool IsReadingOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ReadingToggle));
                OnPropertyChanged(nameof(ReadingToggleName));
            }
        }
    }

    /// <summary>"More" under a cut reading, and "Less" under a whole one.</summary>
    public string ReadingToggle => IsReadingOpen ? "Less" : "More";

    public string ReadingToggleName => IsReadingOpen ? "Show less of the text" : "Show the whole text";

    /// <summary>The text differs from the blueprint's default for the field.</summary>
    public bool CanRevert => Text != Spec.Default;

    /// <summary>Puts the blueprint's default back in one edit.</summary>
    public ICommand RevertCommand { get; }

    internal FieldSpec Spec { get; }

    private string KeyName => $"{char.ToUpperInvariant(Spec.Key[0])}{Spec.Key[1..]}";

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Reading));
        OnPropertyChanged(nameof(CanRevert));
    }
}

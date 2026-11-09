using System.Collections.ObjectModel;
using IDevelop.Desktop.Mvvm;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// One depth of a workflow: all its agents with the same longest path from a start. A stage with every agent Done folds by default,
/// and the person's own fold stays for the session. The Index and Flow read one fold state.
/// </summary>
public sealed class WorkflowStage(int number) : ObservableObject
{
    private IReadOnlyList<TaskNodeViewModel> _members = [];
    private bool? _foldChoice;

    /// <summary>The stage's number from the first stage, 1 upward.</summary>
    public int Number { get; } = number;

    public string Title => $"Stage {Number}";

    /// <summary>The agents the Index lists: every agent of the stage, or none while it is folded.</summary>
    public ObservableCollection<TaskNodeViewModel> Agents { get; } = [];

    public StateCounts Counts { get; private set; }

    /// <summary>"7/8": how many agents of the stage are Done.</summary>
    public string Progress => $"{Counts.Completed}/{Counts.Total}";

    public bool IsFolded => _foldChoice ?? (Counts.Total > 0 && Counts.Completed == Counts.Total);

    /// <summary>What a click on the row does, for a screen reader and the tooltip.</summary>
    public string FoldLabel => $"{(IsFolded ? "Unfold" : "Fold")} {Title} ({Progress})";

    internal bool Holds(TaskNodeViewModel node) => _members.Contains(node);

    /// <summary>Folds an open stage, or opens a folded one, as the person's own choice.</summary>
    public void ToggleFold() => Choose(!IsFolded);

    /// <summary>Opens the stage, so a node in it has its row.</summary>
    public void Open()
    {
        if (IsFolded)
        {
            Choose(false);
        }
    }

    private void Choose(bool folded)
    {
        _foldChoice = folded;
        Show(_members);
    }

    /// <summary>Reads the stage's agents again, after a change of the workflow or of their states.</summary>
    internal void Show(IReadOnlyList<TaskNodeViewModel> members)
    {
        _members = members;
        Counts = StateCounts.Of(members);
        WorkflowCanvasViewModel.Arrange(Agents, IsFolded ? [] : members, null);
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsFolded));
        OnPropertyChanged(nameof(FoldLabel));
    }
}

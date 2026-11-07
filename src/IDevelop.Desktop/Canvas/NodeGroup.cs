using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>The four groups of the left tree's node rows, in the tree's order (ADR 0064).</summary>
public enum NodeGroupKind { NeedsYou, Running, NotStarted, Done }

/// <summary>
/// A group header in an expanded workflow's node list: the group's name, how many nodes it holds, and whether it is folded.
/// A group with more than <see cref="FoldsAbove"/> nodes starts folded, so a large workflow leaves the tree room for the other
/// workflows and projects. "Needs you" always starts open (ADR 0065). A fold that the person sets stays until the person changes it.
/// </summary>
public sealed class NodeGroup(NodeGroupKind kind, Action changed) : ObservableObject
{
    /// <summary>A group other than "Needs you" with more nodes than this starts folded.</summary>
    public const int FoldsAbove = 8;

    private bool? _folded;

    public NodeGroupKind Kind { get; } = kind;

    public string Name => Kind switch
    {
        NodeGroupKind.NeedsYou => "Needs you",
        NodeGroupKind.Running => "Running",
        NodeGroupKind.NotStarted => "Not started",
        _ => "Done",
    };

    /// <summary>How many nodes the group holds.</summary>
    public int Count
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsFolded));
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    /// <summary>The group lists no node rows under its header.</summary>
    public bool IsFolded => _folded ?? (Kind != NodeGroupKind.NeedsYou && Count > FoldsAbove);

    /// <summary>What a click on the header does, such as "Show 21 done nodes".</summary>
    public string ToggleLabel => IsFolded ? $"Show {Name} ({Count})" : $"Fold {Name} ({Count})";

    /// <summary>Folds an open group, or opens a folded one.</summary>
    public void Toggle() => SetFolded(!IsFolded);

    /// <summary>Opens the group, so a node in it has its row.</summary>
    public void Open()
    {
        if (IsFolded)
        {
            SetFolded(false);
        }
    }

    private void SetFolded(bool folded)
    {
        _folded = folded;
        OnPropertyChanged(nameof(IsFolded));
        OnPropertyChanged(nameof(ToggleLabel));
        changed();
    }

    /// <summary>The group that a node in <paramref name="node"/>'s state belongs to. A stale node has no current result, so it has not started its next run.</summary>
    public static NodeGroupKind Of(TaskNodeViewModel node) => node.State switch
    {
        NodeState.InputRequired or NodeState.Failed => NodeGroupKind.NeedsYou,
        NodeState.Running => NodeGroupKind.Running,
        NodeState.Completed => NodeGroupKind.Done,
        _ => NodeGroupKind.NotStarted,
    };
}

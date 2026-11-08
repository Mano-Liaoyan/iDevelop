using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>The groups of the Nodes section's rows, in its order (ADR 0072).</summary>
public enum NodeGroupKind { NeedsYou, Running, Stale, NotStarted, Done }

/// <summary>
/// A group header in the Nodes section's list: the group's name, how many nodes it holds, and whether it is folded. Every group
/// starts open, because the Nodes section has its own scroll and leaves the Projects section its room (ADR 0071). A fold that the
/// person sets stays until the person changes it. A group opens for the selected or open node in it, as a file tree does (ADR 0070).
/// </summary>
public sealed class NodeGroup(NodeGroupKind kind, Action changed) : ObservableObject
{
    private bool _folded;

    public NodeGroupKind Kind { get; } = kind;

    public string Name => Kind switch
    {
        NodeGroupKind.NeedsYou => "Needs you",
        NodeGroupKind.Running => "Running",
        NodeGroupKind.Stale => "Stale",
        NodeGroupKind.NotStarted => "Not started",
        _ => "Done",
    };

    /// <summary>The header takes the state color that a stale card has on the canvas.</summary>
    public bool IsStale => Kind == NodeGroupKind.Stale;

    /// <summary>How many nodes the group holds.</summary>
    public int Count
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    /// <summary>The group lists no node rows under its header.</summary>
    public bool IsFolded => _folded;

    /// <summary>The group lists its rows under its header.</summary>
    public bool ListsRows
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowsFolded));
            }
        }
    }

    /// <summary>The header's chevron points right only when no row shows under it, so the chevron agrees with the rows (ADR 0069).</summary>
    public bool ShowsFolded => !ListsRows;

    /// <summary>What a click on the header does, such as "Show 21 done nodes".</summary>
    public string ToggleLabel => IsFolded ? $"Show {Name} ({Count})" : $"Fold {Name} ({Count})";

    /// <summary>Folds an open group, or opens a folded one.</summary>
    public void Toggle() => SetFolded(!IsFolded);

    /// <summary>Opens the group, so a node in it has its row, and keeps it open as it grows, until the person folds it.</summary>
    public void Open()
    {
        if (_folded)
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

    /// <summary>The group that a node in <paramref name="node"/>'s state belongs to. A stale node has its own group, because the canvas and the run bar name it stale (ADR 0072).</summary>
    public static NodeGroupKind Of(TaskNodeViewModel node) => node.State switch
    {
        NodeState.InputRequired or NodeState.Failed => NodeGroupKind.NeedsYou,
        NodeState.Running => NodeGroupKind.Running,
        NodeState.Stale => NodeGroupKind.Stale,
        NodeState.Completed => NodeGroupKind.Done,
        _ => NodeGroupKind.NotStarted,
    };
}

using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The row after the one node row that a folded group lists, the selected node's. It says how many other nodes the group holds,
/// as "+20 more", and opens the group (ADR 0068).
/// </summary>
public sealed class GroupMore(NodeGroup group) : ObservableObject
{
    public NodeGroup Group { get; } = group;

    /// <summary>How many nodes the folded group does not list, such as "+20 more".</summary>
    public string Label => $"+{Math.Max(0, Group.Count - 1)} more";

    /// <summary>What the row does, for its tooltip and its accessible name.</summary>
    public string ToggleLabel => Group.ToggleLabel;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ToggleLabel));
    }
}

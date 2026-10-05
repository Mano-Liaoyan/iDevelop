using System.Collections.Specialized;

namespace IDevelop.Desktop.Canvas;

/// <summary>While nodes are selected, their connections stand out and every other connection recedes.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    partial void InitializeHighlight()
    {
        SelectedNodes.CollectionChanged += (_, _) => Emphasize(Connections);
        Connections.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                Emphasize(e.NewItems!.Cast<ConnectionViewModel>());
            }
        };
        Emphasize(Connections);
    }

    private void Emphasize(IEnumerable<ConnectionViewModel> connections)
    {
        var selected = SelectedNodes.Select(node => node.Id).ToHashSet();
        foreach (var connection in connections)
        {
            connection.Emphasis = selected.Count == 0 ? WireEmphasis.Normal
                : selected.Contains(connection.Key.From) || selected.Contains(connection.Key.To) ? WireEmphasis.Highlighted
                : WireEmphasis.Dimmed;
        }
    }
}

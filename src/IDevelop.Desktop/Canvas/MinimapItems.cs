using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Data.Converters;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// What the minimap draws: every card, then the ghost cards of open proposals, kept in step with the canvas. The cards
/// come first and in the canvas's order, so a card added or removed moves only its own item.
/// </summary>
public sealed class MinimapItems : ObservableCollection<object>
{
    /// <summary>The minimap's items for a canvas.</summary>
    public static readonly IValueConverter Of = new FuncValueConverter<WorkflowCanvasViewModel?, MinimapItems?>(canvas =>
        canvas is null ? null : new MinimapItems(canvas.Nodes, canvas.Ghosts));

    private readonly ObservableCollection<TaskNodeViewModel> _nodes;
    private readonly ObservableCollection<Ghost> _ghosts;

    private MinimapItems(ObservableCollection<TaskNodeViewModel> nodes, ObservableCollection<Ghost> ghosts)
    {
        _nodes = nodes;
        _ghosts = ghosts;
        Rebuild();
        nodes.CollectionChanged += OnNodesChanged;
        ghosts.CollectionChanged += (_, _) => ShowGhosts();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var i = 0; i < e.NewItems!.Count; i++)
                {
                    Insert(e.NewStartingIndex + i, e.NewItems[i]!);
                }

                break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++)
                {
                    RemoveAt(e.OldStartingIndex);
                }

                break;
            default:
                Rebuild();
                break;
        }
    }

    private void Rebuild()
    {
        Clear();
        foreach (var node in _nodes)
        {
            Add(node);
        }

        ShowGhosts();
    }

    private void ShowGhosts()
    {
        while (Count > _nodes.Count)
        {
            RemoveAt(Count - 1);
        }

        foreach (var card in _ghosts.OfType<GhostCardViewModel>())
        {
            Add(card);
        }
    }
}

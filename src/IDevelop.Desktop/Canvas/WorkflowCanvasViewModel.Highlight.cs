using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace IDevelop.Desktop.Canvas;

/// <summary>While nodes are selected or under the pointer, their connections stand out and every other connection recedes.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    partial void InitializeHighlight()
    {
        SelectedNodes.CollectionChanged += (_, _) =>
        {
            foreach (var node in Nodes)
            {
                node.IsSelected = SelectedNodes.Contains(node);
            }

            Emphasize(Connections);
            Flow.ShowActive();
            // A single selection adds the new node before it removes the old one, so Several waits for the selection to settle.
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowSeveral, Avalonia.Threading.DispatcherPriority.Background);
        };
        Connections.CollectionChanged += (_, e) =>
        {
            OnConnectionsChanged(e);
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                Emphasize(e.NewItems!.Cast<ConnectionViewModel>());
            }
        };
        Emphasize(Connections);
    }

    /// <summary>The card under the pointer in Graph, or null.</summary>
    internal void Hover(TaskNodeViewModel? node)
    {
        if (node != _hovered)
        {
            _hovered = node;
            Emphasize(Connections);
        }
    }

    private TaskNodeViewModel? _hovered;

    private void Emphasize(IEnumerable<ConnectionViewModel> connections)
    {
        var active = SelectedNodes.Select(node => node.Id).ToHashSet();
        if (_hovered is { } hovered)
        {
            active.Add(hovered.Id);
        }

        foreach (var connection in connections)
        {
            connection.Emphasis = active.Count == 0 ? WireEmphasis.Normal
                : active.Contains(connection.Key.From) || active.Contains(connection.Key.To) ? WireEmphasis.Highlighted
                : WireEmphasis.Dimmed;
        }

        ShowPills();
    }

    /// <summary>The ghosts, then a label on each highlighted wire: what the editor draws over the Map.</summary>
    public ObservableCollection<Ghost> Decorations { get; } = [];

    /// <summary>A highlighted wire's label stands on its curve's midpoint.</summary>
    private void ShowPills()
    {
        var pills = Connections.Where(wire => wire.IsHighlighted && wire.Curve.Count > 0)
            .Select(wire => new WirePillViewModel(WireCurve.Middle(wire.Curve), wire.PillText, wire.PillTip)).ToArray();
        if (!pills.SequenceEqual(_pills))
        {
            _pills = pills;
            ShowDecorations();
        }
    }

    private WirePillViewModel[] _pills = [];

    private void ShowDecorations()
    {
        Decorations.Clear();
        foreach (var ghost in Ghosts)
        {
            Decorations.Add(ghost);
        }

        foreach (var pill in _pills)
        {
            Decorations.Add(pill);
        }
    }

    private void ShowSeveral()
    {
        var several = SelectedNodes.Count > 1;
        if (several != _showsSeveral)
        {
            _showsSeveral = several;
            OnPropertyChanged(nameof(Inspected));
        }
    }
}

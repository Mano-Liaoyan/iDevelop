using System.Collections.Specialized;
using System.ComponentModel;

namespace IDevelop.Desktop.Canvas;

/// <summary>Any card can stand in a wire's way, so every wire finds its path again when a card moves, arrives, or leaves.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    partial void InitializeRouting()
    {
        foreach (var node in Nodes)
        {
            node.PropertyChanged += OnCardMoved;
        }

        Nodes.CollectionChanged += (_, e) =>
        {
            foreach (var node in e.OldItems?.Cast<TaskNodeViewModel>() ?? [])
            {
                node.PropertyChanged -= OnCardMoved;
            }

            foreach (var node in e.NewItems?.Cast<TaskNodeViewModel>() ?? [])
            {
                node.PropertyChanged += OnCardMoved;
            }

            Reroute();
        };
        Reroute();
    }

    private void OnCardMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.Location))
        {
            Reroute();
        }
    }

    private void Reroute()
    {
        foreach (var connection in Connections)
        {
            connection.Reroute();
        }
    }
}

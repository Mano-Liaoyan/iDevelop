using System.Collections.Specialized;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the inspector reads from the canvas while nothing is selected, and its way to select a node.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    private void OnConnectionsChanged(NotifyCollectionChangedEventArgs e)
    {
        foreach (var connection in (e.NewItems ?? Array.Empty<object>()).Cast<ConnectionViewModel>().Concat((e.OldItems ?? Array.Empty<object>()).Cast<ConnectionViewModel>()))
        {
            connection.From.OnLinksChanged();
            connection.To.OnLinksChanged();
        }
    }

    public WorkflowOverviewViewModel Overview => field ??= new WorkflowOverviewViewModel(this);

    /// <summary>Selects the node alone, so the inspector shows it.</summary>
    internal void Inspect(TaskNodeViewModel node) => Select(node);
}

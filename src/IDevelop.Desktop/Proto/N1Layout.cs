using Avalonia;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Proto;

// THROWAWAY N1 PROTOTYPE. Never merge. How each option keeps expanded cards off their neighbours, for the view only: the
// workflow's positions never change, so expanding marks nothing unsaved and collapsing puts every card back.
internal static class N1Layout
{
    // The gap the layouts keep between a card and the one they push. A column's 300 px pitch leaves 40 px between
    // containers, and a row's 120 px pitch leaves 56 px between cards.
    private const double ColumnSlack = 40;
    private const double RowGap = 56;

    private static readonly HashSet<WorkflowCanvasViewModel> Pending = [];

    public static void Schedule(WorkflowCanvasViewModel canvas)
    {
        if (Pending.Add(canvas))
        {
            Dispatcher.UIThread.Post(() =>
            {
                Pending.Remove(canvas);
                Relayout(canvas);
            }, DispatcherPriority.Background);
        }
    }

    private static void Relayout(WorkflowCanvasViewModel canvas)
    {
        var nodes = canvas.Nodes.ToList();
        var offsets = TaskNodeViewModel.Option switch
        {
            "a" => ColumnPush(nodes),
            "c" => RowInsert(nodes),
            _ => nodes.ToDictionary(node => node, _ => default(Vector)),
        };
        foreach (var node in nodes)
        {
            node.ViewOffset = offsets[node];
        }

        if (TaskNodeViewModel.IsOptionB)
        {
            N1Notes.Place(canvas);
        }

        foreach (var connection in canvas.Connections)
        {
            connection.Reroute();
        }
    }

    // Columns are cards whose left edges lie within half a card of each other.
    private static List<List<TaskNodeViewModel>> Columns(List<TaskNodeViewModel> nodes)
    {
        var columns = new List<List<TaskNodeViewModel>>();
        foreach (var node in nodes.OrderBy(node => node.Location.X))
        {
            if (columns.Count > 0 && node.Location.X - columns[^1][0].Location.X < WorkflowCanvasViewModel.TaskCardWidth / 2)
            {
                columns[^1].Add(node);
            }
            else
            {
                columns.Add([node]);
            }
        }

        return columns;
    }

    // Each column that holds a wider card moves every column after it right by the extra width.
    private static Dictionary<TaskNodeViewModel, double> ColumnShift(List<TaskNodeViewModel> nodes)
    {
        var shift = new Dictionary<TaskNodeViewModel, double>();
        var total = 0.0;
        foreach (var column in Columns(nodes))
        {
            foreach (var node in column)
            {
                shift[node] = total;
            }

            var widest = column.Max(node => node.MeasuredSize.Width);
            total += Math.Max(0, widest - WorkflowCanvasViewModel.TaskCardWidth);
        }

        return shift;
    }

    // Option A: an expanded card pushes only the cards below it in its own column, keeping each one's original gap.
    private static Dictionary<TaskNodeViewModel, Vector> ColumnPush(List<TaskNodeViewModel> nodes)
    {
        var dx = ColumnShift(nodes);
        var offsets = new Dictionary<TaskNodeViewModel, Vector>();
        foreach (var column in Columns(nodes))
        {
            double? bottom = null;
            TaskNodeViewModel? previous = null;
            foreach (var node in column.OrderBy(node => node.Location.Y))
            {
                var y = node.Location.Y;
                if (bottom is { } b && previous is { } p)
                {
                    var gap = Math.Max(24, node.Location.Y - (p.Location.Y + WorkflowCanvasViewModel.TaskCardHeight));
                    y = Math.Max(y, b + gap);
                }

                offsets[node] = new Vector(dx[node], y - node.Location.Y);
                bottom = y + node.MeasuredSize.Height;
                previous = node;
            }
        }

        return offsets;
    }

    // Option C: an expanded card opens its row like a spreadsheet's: every card whose top lies below the card's header
    // moves down, in every column, so cards that stood level stay level. Two cards that open at once share the space
    // where their bodies overlap, so a card below both moves by the deeper one, not by the two added together.
    private static Dictionary<TaskNodeViewModel, Vector> RowInsert(List<TaskNodeViewModel> nodes)
    {
        var dx = ColumnShift(nodes);
        var shifts = new Dictionary<TaskNodeViewModel, double>();
        var carry = 0.0;
        foreach (var level in nodes.GroupBy(node => node.Location.Y).OrderBy(group => group.Key))
        {
            var above = shifts.Where(pair => pair.Key.IsExpanded && pair.Key.Location.Y + WorkflowCanvasViewModel.TaskCardHeight <= level.Key + 0.5)
                .Select(pair => pair.Value + pair.Key.MeasuredSize.Height - WorkflowCanvasViewModel.TaskCardHeight);
            carry = Math.Max(carry, above.DefaultIfEmpty(0).Max());
            foreach (var node in level)
            {
                shifts[node] = carry;
            }
        }

        return nodes.ToDictionary(node => node, node => new Vector(dx[node], shifts[node]));
    }
}

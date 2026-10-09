using System.Collections.Immutable;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Where each task stands between runs (#90). The canvas reads the workflow's runs off the UI thread when the project opens
/// and whenever a run it shows settles, and judges them against the workflow as it is now, so an edit puts a result out of
/// date at once and a restart keeps what the cards showed.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private ImmutableArray<RunRecord> _earlier = [];
    private RunHistory? _history;
    private int _historyReads;
    private RunAddress? _settledRead;

    /// <summary>What the workflow's settled runs left its tasks, as this window read them last, or null before the first read.</summary>
    internal RunHistory? History => _history;

    /// <summary>Reads the workflow's runs again and shows where each task stands. A newer read wins over an older one.</summary>
    internal async void ReadHistory()
    {
        var read = ++_historyReads;
        var workflow = Workflow.Id;
        ImmutableArray<RunRecord> records;
        try
        {
            records = await Task.Run(() => Runs.EarlierRuns(workflow));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (read != _historyReads)
        {
            return;
        }

        _earlier = records;
        ShowHistory();
    }

    /// <summary>Gives every card where its task stands now, after an edit or a new read. Nothing before the first read.</summary>
    private void ShowHistory()
    {
        if (_historyReads == 0)
        {
            return;
        }

        var workflow = Workflow;
        var history = RunHistory.Of(_earlier, workflow);
        _history = history;
        foreach (var node in Nodes)
        {
            node.ShowStanding(TaskStanding.Of(history, workflow, node.Id));
            node.OnHistoryChanged();
        }

        OnPropertyChanged(nameof(History));
    }

    /// <summary>A run the canvas shows settled: its results now count between runs, so the canvas reads them once.</summary>
    private void ReadSettledRun()
    {
        if (Run is { IsActive: false } run && run.Address != _settledRead)
        {
            _settledRead = run.Address;
            ReadHistory();
        }
    }
}

using IDevelop.Desktop.Execution;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The node between runs (#90): where its task stands after the runs of its workflow that settled, as Succeeded, Out of
/// date, how its newest run ended it without a result, or Waits for the tasks before it. The card and the inspector keep it after a run and after a restart, while no
/// run says more about the task and the task has not run on its own since.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private TaskStanding? _standing;
    private TaskStanding? _shownStanding;

    /// <summary>
    /// Where the task stands between runs, while nothing newer says more, or null. A settled run still on the cards keeps
    /// what it says of a task it ran, until an edit puts that result out of date.
    /// </summary>
    internal TaskStanding? Standing => _standing is { } standing &&
        (RunTask is not { } run || RunSaysNothing(run) || !_runActive && standing is TaskStanding.OutOfDate) && !RanOnItsOwnSince(standing)
        ? standing : null;

    /// <summary>Called on the UI thread with where the task stands after the workflow's settled runs, or null.</summary>
    internal void ShowStanding(TaskStanding? standing)
    {
        if (Equals(standing, _standing))
        {
            return;
        }

        _standing = standing;
        OnRunChanged();
        OnPropertyChanged(nameof(Attention));
    }

    /// <summary>The workflow's history changed, which can change the tasks this one runs after.</summary>
    internal void OnHistoryChanged()
    {
        OnPropertyChanged(nameof(StartProblem));
        OnPropertyChanged(nameof(RunRefusal));
        OnPropertyChanged(nameof(RunOwner));
        _run.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The run shows the task only as not started: nobody ran it in the active run, or a settled run never started it. Then
    /// where the task stands between runs says more.
    /// </summary>
    private bool RunSaysNothing(TaskView run) => WorkflowRunText.NotStarted(run, _runActive);

    /// <summary>The task ran on its own after the run that left it where it stands, so that attempt shows instead.</summary>
    private bool RanOnItsOwnSince(TaskStanding standing) => _attempt is { } attempt && standing switch
    {
        TaskStanding.Succeeded succeeded => attempt.RequestedAt > succeeded.At,
        TaskStanding.OutOfDate outOfDate => attempt.RequestedAt > outOfDate.At,
        TaskStanding.Ended ended => attempt.RequestedAt > ended.At,
        _ => true,
    };

    /// <summary>The task's own latest attempt changed, which can end what the node shows between runs.</summary>
    partial void InitializeStanding()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LastAttempt) && !Equals(_shownStanding, Standing))
            {
                OnRunChanged();
            }
        };
    }
}

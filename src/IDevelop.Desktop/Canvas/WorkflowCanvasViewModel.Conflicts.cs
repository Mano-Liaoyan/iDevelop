using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A merge conflict that no Merge node takes waits for the person (issue 148). The card and the inspector of the task whose
/// predecessors conflict, and the run's bar for the final merge, offer to open the conflict's worktree and to mark it
/// resolved once its files are fixed. Adding a Merge node and running it stays the other way.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private RelayCommand? _openRunConflict;
    private RelayCommand? _markRunConflictResolved;
    private bool _resolvingConflict;

    /// <summary>
    /// Opens a folder in the system's file manager, and returns whether it opened. The window sets it; without one, nothing
    /// opens and the notice names the folder.
    /// </summary>
    internal Func<string, Task<bool>> OpenFolder { get; set; } = _ => Task.FromResult(false);

    /// <summary>The run's final merge conflicts, and no Merge node takes it, so the run's bar offers the person's way.</summary>
    public bool ShowsRunConflict => Run is { IsControlled: true } run && WorkflowRunCoordinator.PersonsConflict(run.View) is not null;

    /// <summary>Opens the worktree of the final merge's conflict.</summary>
    public ICommand OpenRunConflictCommand => _openRunConflict ??= new RelayCommand(
        () => _ = OpenConflictAsync(Run is { } run ? WorkflowRunCoordinator.PersonsConflict(run.View) : null), () => ShowsRunConflict && !_resolvingConflict);

    /// <summary>Commits the person's files in the final merge's conflict worktree as the merge.</summary>
    public ICommand MarkRunConflictResolvedCommand => _markRunConflictResolved ??= new RelayCommand(
        () => _ = MarkResolvedAsync(Run is { } run ? WorkflowRunCoordinator.PersonsConflict(run.View) : null), () => ShowsRunConflict && !_resolvingConflict);

    /// <summary>Whether a step on a conflict is under way, so another waits for it.</summary>
    internal bool ResolvingConflict => _resolvingConflict;

    /// <summary>Opens <paramref name="conflict"/>'s worktree and says where it is and what to do there.</summary>
    internal async Task OpenConflictAsync(MergeConflict? conflict)
    {
        if (conflict is null || Run is not { } run || !Begin())
        {
            return;
        }

        try
        {
            var reply = await WorkflowRunViewModel.Retrying(() => run.Coordinator.OpenConflict(run.Address, conflict),
                outcome => outcome is ConflictReply.Refused refused && WorkflowRunText.Transient(refused.Reason));
            if (reply is ConflictReply.Opened opened)
            {
                bool shown;
                try
                {
                    shown = await OpenFolder(opened.Folder);
                }
                catch (Exception)
                {
                    shown = false;
                }

                Notice(WorkflowRunText.ConflictOpened(opened.Folder, conflict.Paths, shown));
            }
            else
            {
                Notice(WorkflowRunText.Conflict(reply));
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>Marks <paramref name="conflict"/> resolved with the person's files, and says why when it is refused.</summary>
    internal async Task MarkResolvedAsync(MergeConflict? conflict)
    {
        if (conflict is null || Run is not { } run || !Begin())
        {
            return;
        }

        try
        {
            var confirmation = new OperationId(Guid.NewGuid());
            var reply = await WorkflowRunViewModel.Retrying(() => run.Coordinator.MarkResolved(run.Address, conflict, confirmation),
                outcome => outcome is ConflictReply.Refused refused && WorkflowRunText.Transient(refused.Reason));
            Notice(WorkflowRunText.Conflict(reply));
        }
        finally
        {
            End();
        }
    }

    private bool Begin()
    {
        if (_resolvingConflict)
        {
            return false;
        }

        _resolvingConflict = true;
        NotifyConflictCommands();
        return true;
    }

    private void End()
    {
        _resolvingConflict = false;
        NotifyConflictCommands();
    }

    /// <summary>The run's bar and each card follow the run's conflicts that wait for the person.</summary>
    private void NotifyConflictCommands()
    {
        OnPropertyChanged(nameof(ShowsRunConflict));
        _openRunConflict?.NotifyCanExecuteChanged();
        _markRunConflictResolved?.NotifyCanExecuteChanged();
        foreach (var node in Nodes)
        {
            node.NotifyConflict();
        }
    }
}

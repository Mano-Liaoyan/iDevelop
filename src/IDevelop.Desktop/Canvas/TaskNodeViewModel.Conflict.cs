using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A conflict between the task's predecessors that no Merge node takes waits for the person (issue 148). The card and the
/// inspector offer to open the conflict's worktree, and to mark it resolved once its files are fixed, so the task starts
/// from the person's merge.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private RelayCommand? _openConflict;
    private RelayCommand? _markResolved;

    /// <summary>The conflict between the task's predecessors that waits for the person, in the run this window controls.</summary>
    private MergeConflict? PersonsConflict => _runActive && _canvas.Run?.View is { Phase: RunPhase.Approved, Controlled: true }
        ? WorkflowRunCoordinator.PersonsConflict(RunTask) : null;

    /// <summary>The card and the inspector offer the person's way to resolve the conflict.</summary>
    public bool ShowsConflictActions => PersonsConflict is not null;

    /// <summary>Opens the conflict's worktree, detached at the merge with its conflict markers.</summary>
    public ICommand OpenConflictCommand => _openConflict ??= new RelayCommand(() => _ = _canvas.OpenConflictAsync(PersonsConflict),
        () => ShowsConflictActions && !_canvas.ResolvingConflict);

    /// <summary>Commits the person's files in the conflict's worktree as the merge, which the task then starts from.</summary>
    public ICommand MarkResolvedCommand => _markResolved ??= new RelayCommand(() => _ = _canvas.MarkResolvedAsync(PersonsConflict),
        () => ShowsConflictActions && !_canvas.ResolvingConflict);

    /// <summary>The canvas says when the run changed, or a step on a conflict starts or ends.</summary>
    internal void NotifyConflict()
    {
        OnPropertyChanged(nameof(ShowsConflictActions));
        _openConflict?.NotifyCanExecuteChanged();
        _markResolved?.NotifyCanExecuteChanged();
    }
}

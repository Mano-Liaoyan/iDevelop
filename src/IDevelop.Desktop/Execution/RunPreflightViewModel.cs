using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>A task as the preflight lists it: its kind, its title, the agent it runs with, and what it waits for.</summary>
public sealed record PreflightTaskRow(NodeKind Kind, string Title, string Agent, string? Inputs);

/// <summary>Something that keeps the workflow from starting now, with a way to the node it names, if any.</summary>
public sealed record PreflightGapRow(string Text, ICommand? ShowCommand)
{
    public bool HasNode => ShowCommand is not null;
}

/// <summary>
/// Run Workflow's preflight: what the run would approve and how it would start, for the whole workflow, without a selected
/// node. It reads the project off the UI thread and records nothing. Start confirms what it shows; repeating Start, or
/// starting again after a refusal, confirms with the same command, so the sheet approves one run at most.
/// </summary>
public sealed class RunPreflightViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly OperationId _command = new(Guid.NewGuid());
    private readonly RelayCommand _start;
    private readonly RelayCommand _showActive;
    private RunPreflight? _preview;
    private bool _checking = true;
    private bool _starting;
    private bool _detached;
    private bool _useSnapshot;
    private string? _notice;
    private RunId? _active;

    internal RunPreflightViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        _start = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        _showActive = new RelayCommand(ShowActive, () => _active is not null && !_starting);
        CancelCommand = new RelayCommand(canvas.ClosePreflight);
        _ = CheckAsync();
    }

    public string WorkflowName => _canvas.Name;

    /// <summary>The project is being read, so nothing else shows yet.</summary>
    public bool IsChecking => _checking;

    public bool IsReady => !_checking && _preview is not null;

    public IReadOnlyList<PreflightTaskRow> Tasks => _preview is { } preview ? [.. preview.Tasks.Select(Row)] : [];

    public IReadOnlyList<PreflightGapRow> Gaps => _preview is { } preview ? [.. GapRows(preview)] : [];

    public bool HasGaps => Gaps.Count > 0;

    /// <summary>The project has uncommitted work, so the person picks HEAD or a snapshot of that work.</summary>
    public bool OffersSnapshot => _preview?.Choices.Contains(BaseChoice.Snapshot) ?? false;

    public bool UseSnapshot
    {
        get => _useSnapshot;
        set
        {
            if (SetProperty(ref _useSnapshot, value))
            {
                OnPropertyChanged(nameof(UseHead));
                _start.NotifyCanExecuteChanged();
            }
        }
    }

    public bool UseHead
    {
        get => !UseSnapshot;
        set => UseSnapshot = !value;
    }

    /// <summary>"HEAD, a1b2c3d on main", the commit a run starts from without a snapshot.</summary>
    public string? HeadLabel => _preview?.Base is { } found
        ? $"HEAD, {Short(found.Head)}{(found.Branch is { } branch ? $" on {branch.Replace("refs/heads/", "", StringComparison.Ordinal)}" : "")}"
        : null;

    public string SnapshotLabel => "A snapshot of the uncommitted work";

    /// <summary>What differs from HEAD, which only a snapshot takes along, or null for a clean project.</summary>
    public string? ChangedNote => _preview?.Base is { Changed: { IsEmpty: false } changed }
        ? $"{Count(changed.Length, "path")} {(changed.Length == 1 ? "differs" : "differ")} from HEAD: {List(changed)}. A snapshot takes them along without changing your branch, index, or files."
        : null;

    public string? IgnoredNote => _preview?.Base is { } found
        ? found.Ignored.IsEmpty ? "Ignored files never reach a task." : $"Ignored files never reach a task, such as {List(found.Ignored)}."
        : null;

    public string? SubmodulesNote => _preview?.Base is { Submodules: { IsEmpty: false } submodules }
        ? $"Each task's checkout initializes its own submodules: {List([.. submodules.Select(submodule => submodule.Path)])}."
        : null;

    public string? WorktreeNote => _preview is { } preview
        ? $"Each task works in its own checkout under {preview.Worktrees.Checkouts}/, on a branch under {preview.Worktrees.Branches}. Your branch, index, and files stay as they are."
        : null;

    /// <summary>Earlier reports the run could reuse. Listing one includes nothing.</summary>
    public string? ReusableNote => _preview is { Reusable: { IsEmpty: false } reusable } preview
        ? $"Earlier reports of {List([.. reusable.Select(report => Title(preview, report.Task))])} could be reused. This run starts every task again."
        : null;

    /// <summary>Why the last Start started nothing, or what changed since the preview, or null.</summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public bool IsStarting => _starting;

    public bool CanStart => IsReady && !_starting && !HasGaps && _preview!.Choices.Contains(Choice);

    /// <summary>Another run of this workflow is active, so Start would not approve a new one. This shows that run instead.</summary>
    public bool ShowsActive => _active is not null;

    public ICommand StartCommand => _start;

    public ICommand CancelCommand { get; }

    public ICommand ShowActiveCommand => _showActive;

    private BaseChoice Choice => UseSnapshot ? BaseChoice.Snapshot : BaseChoice.Head;

    /// <summary>The sheet closed. A Start in flight still shows its run.</summary>
    internal void Detach() => _detached = true;

    private async Task CheckAsync()
    {
        var workflow = _canvas.Workflow;
        var runs = _canvas.Runs;
        RunPreflight preview;
        try
        {
            preview = await Task.Run(() => runs.Preflight(workflow));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Show(preview);
    }

    private void Show(RunPreflight preview)
    {
        _preview = preview;
        _checking = false;
        _active ??= preview.Active;
        if (!preview.Choices.Contains(Choice))
        {
            _useSnapshot = false;
        }

        foreach (var property in new[]
        {
            nameof(IsChecking), nameof(IsReady), nameof(Tasks), nameof(Gaps), nameof(HasGaps), nameof(OffersSnapshot), nameof(UseSnapshot), nameof(UseHead),
            nameof(HeadLabel), nameof(ChangedNote), nameof(IgnoredNote), nameof(SubmodulesNote), nameof(WorktreeNote), nameof(ReusableNote),
            nameof(CanStart), nameof(ShowsActive),
        })
        {
            OnPropertyChanged(property);
        }

        _start.NotifyCanExecuteChanged();
        _showActive.NotifyCanExecuteChanged();
    }

    private async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        SetStarting(true);
        Notice = null;
        var confirmation = new RunConfirmation(_preview!, Choice, _command);
        var current = _canvas.Workflow;
        WorkflowStart start;
        try
        {
            start = await WorkflowRunViewModel.Retrying(
                () => _canvas.Runs.StartWorkflow(current, confirmation),
                outcome => outcome is WorkflowStart.Refused { Problem: ApprovalProblem.ApprovalBusy });
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            SetStarting(false);
        }

        switch (start)
        {
            case WorkflowStart.Started started:
                _canvas.Adopt(started.Coordinator);
                if (!_detached)
                {
                    _canvas.ClosePreflight();
                }

                _canvas.Notice(started.Resume switch
                {
                    RunCommand.Unavailable unavailable => unavailable.Message,
                    RunCommand.Refused refused => WorkflowRunText.Problem(refused.Reason),
                    _ => null,
                });
                break;
            case WorkflowStart.Changed changed:
                Show(changed.Current);
                Notice = "The workflow or the project changed since this preview. Check it again, then start.";
                break;
            case WorkflowStart.Refused refused:
                if (refused.Current is { } now)
                {
                    Show(now);
                }

                Notice = refused.Problem switch
                {
                    ApprovalProblem.ApprovalBusy => "Another confirmation of this workflow is still running. Start again in a moment.",
                    ApprovalProblem.NotConfirmable => "The preview has something to fix first.",
                    ApprovalProblem.GitFailed => $"Git could not record the run's base. {refused.Detail}",
                    _ => $"iDevelop could not approve the run. {refused.Detail}",
                };
                break;
            case WorkflowStart.Busy busy:
                _active = busy.Active;
                Notice = "Another run of this workflow is still active, with other content. Show it to stop it or let it finish.";
                OnPropertyChanged(nameof(ShowsActive));
                _showActive.NotifyCanExecuteChanged();
                break;
            case WorkflowStart.Unopened unopened:
                _active = unopened.Run;
                Notice = $"The run was approved, but this window could not open it. {WorkflowRunText.Problem(unopened.Reason)}";
                OnPropertyChanged(nameof(ShowsActive));
                _showActive.NotifyCanExecuteChanged();
                break;
        }
    }

    private void SetStarting(bool starting)
    {
        _starting = starting;
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(CanStart));
        _start.NotifyCanExecuteChanged();
        _showActive.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the active run in this window, which controls it unless another window does, and closes the sheet.</summary>
    private void ShowActive()
    {
        if (_active is not { } run)
        {
            return;
        }

        RunOpen open;
        try
        {
            open = _canvas.Runs.OpenRun(_canvas.Workflow.Id, run);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (open is RunOpen.Opened opened)
        {
            _canvas.Adopt(opened.Coordinator);
            _canvas.ClosePreflight();
        }
        else if (open is RunOpen.Rejected rejected)
        {
            Notice = $"This window could not open the active run. {WorkflowRunText.Problem(rejected.Reason)}";
        }
    }

    private PreflightTaskRow Row(PreflightTask task)
    {
        var node = _canvas.Nodes.FirstOrDefault(candidate => candidate.Id == task.Task);
        var agent = task.Kind == WorkKind.Person ? "Waits for your approval"
            : RunText.AgentLabel(task.Settings, task.Settings is { } settings ? _canvas.Clients.Current[settings.Client] : new ClientStatus.Checking());
        var inputs = task.Inputs.IsEmpty ? null
            : $"After {string.Join(", ", task.Inputs.Select(input => input.Kind == ConnectionKind.Context ? $"{Title(_preview!, input.From)} (context)" : Title(_preview!, input.From)))}";
        return new PreflightTaskRow(node?.Kind ?? NodeKind.Implement, task.Title, agent, inputs);
    }

    private IEnumerable<PreflightGapRow> GapRows(RunPreflight preview)
    {
        if (preview.Tasks.IsEmpty)
        {
            yield return new("The workflow has no tasks yet. Add a node first.", null);
        }

        foreach (var gap in preview.Gaps)
        {
            yield return gap switch
            {
                PreflightGap.Task task => new($"{Title(preview, task.Id)}: {RunText.Describe(task.Problem)}", ShowNode(task.Id)),
                PreflightGap.Join join => new($"{Title(preview, join.Id)}: {join.Detail}", ShowNode(join.Id)),
                PreflightGap.NoCommit => new("The project has no commit yet. Commit once, then run the workflow.", null),
                PreflightGap.Git git => new(git.Problem == MaterializationProblem.NotRepositoryRoot
                    ? $"Workflow runs need the project folder to be a Git repository's root. {git.Detail}" : git.Detail, null),
                PreflightGap.Submodules submodules => new($"Git could not read the project's submodules. {submodules.Detail}", null),
                PreflightGap.Records records => new($"A run record of this workflow could not be read. {records.Detail}", null),
                _ => new(gap.ToString(), null),
            };
        }
    }

    /// <summary>Closes the sheet and selects the node, so the inspector shows what to fix.</summary>
    private RelayCommand? ShowNode(TaskId task) => _canvas.Nodes.FirstOrDefault(node => node.Id == task) is { } node
        ? new RelayCommand(() =>
        {
            _canvas.ClosePreflight();
            _canvas.Inspect(node);
        })
        : null;

    private static string Title(RunPreflight preview, TaskId task) =>
        preview.Tasks.FirstOrDefault(row => row.Task == task)?.Title is { Length: > 0 } title ? $"\"{title}\"" : "A task";

    private static string Short(CommitId commit) => commit.Hex.Length > 7 ? commit.Hex[..7] : commit.Hex;

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string List(IReadOnlyList<string> items) => items.Count <= 3
        ? string.Join(", ", items)
        : $"{string.Join(", ", items.Take(3))}, and {items.Count - 3} more";
}

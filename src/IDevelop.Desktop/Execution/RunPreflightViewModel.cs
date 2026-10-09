using System.Collections.Immutable;
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
/// A result from an earlier run that a node's run uses instead of running its task again (#90), or one it cannot use on
/// the chosen base and why.
/// </summary>
public sealed record PreflightCarriedRow(string Text, bool IsUsed);

/// <summary>
/// An earlier report that the run can include instead of running its task again: a root planner's plan, or another root
/// task's report. Listing it includes nothing; the person checks it.
/// </summary>
public sealed class PreflightInclusionRow : ObservableObject
{
    private readonly ImmutableArray<BaseChoice> _bases;
    private readonly Func<BaseChoice> _choice;
    private bool _isIncluded;

    internal PreflightInclusionRow(ReportInclusion inclusion, string label, string? report, string? note, ImmutableArray<BaseChoice> bases,
        Func<BaseChoice> choice)
    {
        Inclusion = inclusion;
        Label = label;
        Report = report;
        Note = note;
        _bases = bases;
        _choice = choice;
    }

    public string Label { get; }

    /// <summary>The start of the report the run would hand on.</summary>
    public string? Report { get; }

    /// <summary>What including it does besides, or why it cannot be included.</summary>
    public string? Note { get; }

    /// <summary>The run can include it on the chosen base.</summary>
    public bool IsAvailable => _bases.Contains(_choice());

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    internal ReportInclusion Inclusion { get; }

    internal void OnChoiceChanged() => OnPropertyChanged(nameof(IsAvailable));
}

/// <summary>
/// The preflight of Run Workflow, for the whole workflow without a selected node, or of a node's Run, for that node and the
/// tasks after it (#90): what the run would approve and how it would start. It reads the project off the UI thread and
/// records nothing. Start confirms what it shows; repeating Start, or starting again after a refusal, confirms with the
/// same command, so the sheet approves one run at most.
/// </summary>
public sealed class RunPreflightViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly TaskId? _node;
    private readonly OperationId _command = new(Guid.NewGuid());
    private readonly RelayCommand _start;
    private readonly RelayCommand _showActive;
    private RunPreflight? _preview;
    private bool _checking = true;
    private bool _starting;
    private bool _opening;
    private bool _detached;
    private bool _useSnapshot;
    private string? _notice;
    private RunId? _active;

    /// <param name="node">The node whose Run opened the sheet, or null for Run Workflow.</param>
    internal RunPreflightViewModel(WorkflowCanvasViewModel canvas, TaskId? node = null)
    {
        _canvas = canvas;
        _node = node;
        _start = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        _showActive = new RelayCommand(ShowActive, () => _active is not null && !_starting && !_opening);
        CancelCommand = new RelayCommand(canvas.ClosePreflight);
        _ = CheckAsync();
    }

    public string WorkflowName => _canvas.Name;

    /// <summary>"Run Workflow", or "Run "Parse input"" for a node's Run.</summary>
    public string Heading => _node is { } node ? $"Run \"{_canvas.Workflow.Tasks.GetValueOrDefault(node)?.Title ?? "a removed task"}\"" : "Run Workflow";

    /// <summary>The node whose Run opened the sheet, or null for Run Workflow.</summary>
    internal TaskId? Node => _node;

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
                OnPropertyChanged(nameof(Carried));
                foreach (var row in Inclusions)
                {
                    row.OnChoiceChanged();
                }

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

    /// <summary>Earlier reports the run can include instead of running their tasks again. Listing one includes nothing.</summary>
    public IReadOnlyList<PreflightInclusionRow> Inclusions { get; private set; } = [];

    public bool HasInclusions => Inclusions.Count > 0;

    /// <summary>
    /// The results of earlier runs a node's run uses for the tasks before it that it does not run, on the chosen base, and
    /// the ones it cannot use there (#90). Run Workflow uses none.
    /// </summary>
    public IReadOnlyList<PreflightCarriedRow> Carried => _preview is { } preview ? [.. preview.Carried.Select(row => CarriedRow(preview, row))] : [];

    /// <summary>The sheet lists earlier results: ones the run uses, or reports it can include.</summary>
    public bool HasEarlierResults => Carried.Count > 0 || HasInclusions;

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
            preview = await Task.Run(() => runs.Preflight(workflow, _node));
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

        // A new preview keeps what the person included, when it still offers the same report.
        var included = Inclusions.Where(row => row.IsIncluded).Select(row => row.Inclusion).ToHashSet();
        Inclusions = [.. InclusionRows(preview)];
        foreach (var row in Inclusions)
        {
            row.IsIncluded = included.Contains(row.Inclusion);
        }

        foreach (var property in new[]
        {
            nameof(IsChecking), nameof(IsReady), nameof(Tasks), nameof(Gaps), nameof(HasGaps), nameof(OffersSnapshot), nameof(UseSnapshot), nameof(UseHead),
            nameof(HeadLabel), nameof(ChangedNote), nameof(IgnoredNote), nameof(SubmodulesNote), nameof(WorktreeNote), nameof(Inclusions), nameof(HasInclusions),
            nameof(Carried), nameof(HasEarlierResults),
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
        var confirmation = new RunConfirmation(_preview!, Choice, _command)
        {
            Include = [.. Inclusions.Where(row => row.IsIncluded && row.IsAvailable).Select(row => row.Inclusion)],
        };
        var current = _canvas.Workflow;
        WorkflowStart start;
        try
        {
            start = await WorkflowRunViewModel.Retrying(
                () => _canvas.Runs.StartWorkflow(current, confirmation),
                outcome => outcome is WorkflowStart.Refused refused && WorkflowRunText.Transient(refused.Problem, refused.Detail));
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
                    ApprovalProblem.InclusionRefused =>
                        $"An included report can no longer be used. {WorkflowRunText.ApprovalDetail(refused.Detail)} Clear it to run its task again.",
                    _ => $"iDevelop could not approve the run. {WorkflowRunText.ApprovalDetail(refused.Detail)}",
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
    private async void ShowActive()
    {
        if (_active is not { } run || _opening)
        {
            return;
        }

        _opening = true;
        _showActive.NotifyCanExecuteChanged();
        var open = await WorkflowRunViewModel.OpenAsync(_canvas.Runs, _canvas.Workflow.Id, run);
        _opening = false;
        _showActive.NotifyCanExecuteChanged();
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
            : $"After {string.Join(", ", task.Inputs.OrderBy(input => TitleOf(_preview!, input.From), StringComparer.CurrentCultureIgnoreCase).ThenBy(input => input.From)
                .Select(input => input.Kind == ConnectionKind.Context ? $"{Title(_preview!, input.From)} (context)" : Title(_preview!, input.From)))}";
        return new PreflightTaskRow(node?.Kind ?? NodeKind.Implement, task.Title, agent, inputs);
    }

    private IEnumerable<PreflightInclusionRow> InclusionRows(RunPreflight preview)
    {
        foreach (var planner in preview.Planners)
        {
            var note = planner.Problem is { } problem ? $"It cannot be included: {WorkflowRunText.Problem(new RunRejection(problem))}"
                : planner.Status == AttemptStatus.WaitingForInput ? "It waits for you. Including it marks it done when the run starts."
                : null;
            yield return new(new ReportInclusion(planner.Task, planner.Source, planner.Turn),
                $"Include {Title(preview, planner.Task)}'s plan instead of running it again", Excerpt(planner.Report), note, planner.Bases, () => Choice);
        }

        foreach (var report in preview.Reusable.Where(report => preview.Planners.All(planner => planner.Task != report.Task)))
        {
            yield return new(new ReportInclusion(report.Task, report.Source, 1),
                $"Reuse {Title(preview, report.Task)}'s earlier report instead of running it again", Excerpt(report.Report), null, report.Bases, () => Choice);
        }
    }

    /// <summary>
    /// "Uses "B"'s result from an earlier run." for a result the run carries on the chosen base, else why it cannot, after
    /// which the tasks after it wait until it runs again (#90).
    /// </summary>
    private PreflightCarriedRow CarriedRow(RunPreflight preview, PreflightCarried row)
    {
        var task = Title(preview, row.Task);
        if (row.Bases.Contains(Choice))
        {
            return new(WorkflowRunText.Unbroken($"Uses {task}'s result from an earlier\u00A0run."), true);
        }

        var why = row.Refusal switch
        {
            CarryRefusal.Conflict conflict => $"its code conflicts with this base{In(conflict.Paths)}",
            CarryRefusal.JoinConflict join => $"the results it used conflict with each other on this base{In(join.Paths)}",
            CarryRefusal.InputRuns runs => $"it used a result of {Title(preview, runs.Input)}, which this run runs again",
            CarryRefusal.InputRefused refused => $"it used a result of {Title(preview, refused.Input)}, which this run cannot use either",
            CarryRefusal.Unavailable unavailable => unavailable.Detail.TrimEnd('.'),
            _ => "this base does not offer it",
        };
        return new(WorkflowRunText.Unbroken($"Can't use {task}'s result from an earlier run: {why}. The tasks after it wait until it runs\u00A0again."), false);
    }

    private static string In(IReadOnlyList<string> paths) => paths.Count == 0 ? "" : $" in {List(paths)}";

    /// <summary>The report's first three lines that have text.</summary>
    private static string? Excerpt(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return null;
        }

        var lines = report.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToArray();
        return lines.Length <= 3 ? string.Join("\n", lines) : string.Join("\n", lines.Take(3)) + " …";
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
                PreflightGap.After after => new($"{Title(preview, after.Id)}: {WorkflowRunText.RunsAfter(after.Predecessors, task => TitleOf(preview, task))}", ShowNode(after.Id)),
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

    /// <summary>A task by its title in the previewed workflow, which also holds the tasks a node's preview does not list.</summary>
    private static string Title(RunPreflight preview, TaskId task) =>
        preview.Revision.Snapshot.Tasks.GetValueOrDefault(task)?.Title is { Length: > 0 } title ? $"\"{title}\"" : "A task";

    /// <summary>A task's title in the previewed workflow, unquoted, for text that quotes it.</summary>
    private static string TitleOf(RunPreflight preview, TaskId task) => preview.Revision.Snapshot.Tasks.GetValueOrDefault(task)?.Title ?? "a removed task";

    private static string Short(CommitId commit) => commit.Hex.Length > 7 ? commit.Hex[..7] : commit.Hex;

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string List(IReadOnlyList<string> items) => items.Count <= 3
        ? string.Join(", ", items)
        : $"{string.Join(", ", items.Take(3))}, and {items.Count - 3} more";
}

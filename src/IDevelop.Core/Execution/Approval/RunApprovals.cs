using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Run Workflow's preflight and its approval. A run starts from the current branch's HEAD. <see cref="Inspect"/> records
/// nothing; finding what differs from HEAD leaves only unreferenced loose objects in Git's object database.
/// </summary>
internal sealed class RunApprovals
{
    private static readonly string[] Excluded = [".idp", ".worktrees"];

    private readonly string _project;
    private readonly RunStore _store;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly IReadOnlyDictionary<ClientId, ClientStatus> _clients;
    private readonly Action<string>? _probe;
    private readonly IReadOnlyDictionary<TaskId, AttemptRecord> _latest;
    private readonly Func<TaskId, TurnKey, StartProblem?>? _finish;
    private readonly Func<ExecutionSettings?, SkillListing?>? _skills;

    /// <param name="latest">The newest standalone attempt of each task, whose report the preview may list as reusable.</param>
    /// <param name="finish">Marks a waiting standalone attempt done at its turn, or says why it cannot.</param>
    /// <param name="skills">The skills a client lists in the project, which a task with a skill choice needs.</param>
    public RunApprovals(string project, RunStore store, IReadOnlyDictionary<string, string>? environment,
        IReadOnlyDictionary<ClientId, ClientStatus> clients, IReadOnlyDictionary<TaskId, AttemptRecord> latest,
        Action<string>? probe = null, Func<TaskId, TurnKey, StartProblem?>? finish = null, Func<ExecutionSettings?, SkillListing?>? skills = null)
    {
        _skills = skills;
        _probe = probe;
        _latest = latest;
        _finish = finish;
        _project = Path.GetFullPath(project);
        _store = store;
        _environment = environment ?? new Dictionary<string, string>();
        _clients = clients;
    }

    /// <param name="node">
    /// The node whose Run asks for the run, or null for Run Workflow (#90). The preview then lists, checks, and offers only
    /// that node and the tasks after it, and names the predecessors it would wait for.
    /// </param>
    public RunPreflight Inspect(Workflow workflow, TaskId? node = null)
    {
        var revision = Revision.Capture(workflow);
        var scope = RunScope.InFlow(workflow, node is { } requested ? new HashSet<TaskId> { requested } : null);
        // A node's preview lists the node first, then the tasks after it.
        ImmutableArray<PreflightTask> tasks = [.. workflow.Tasks.Values.Where(task => scope.Contains(task.Id)).OrderBy(task => task.Id != node)
            .ThenBy(task => task.Id).Select(task => Task(workflow, task))];
        var gaps = ImmutableArray.CreateBuilder<PreflightGap>();
        var open = GitRepository.Open(_project, _environment);
        GitRepository? repository = null;
        PreflightGit git;
        if (open is RepositoryOpen.Refused refused)
        {
            git = new PreflightGit.Refused(refused.Problem, refused.Detail);
            gaps.Add(new PreflightGap.Git(refused.Problem, refused.Detail));
        }
        else
        {
            repository = ((RepositoryOpen.Opened)open).Repository;
            git = new PreflightGit.Ready(repository.Version);
        }
        foreach (var task in tasks)
        {
            if (Gap(workflow.Tasks[task.Task]) is { } problem) gaps.Add(new PreflightGap.Task(task.Task, problem));
        }
        PreflightBase? found = null;
        try { found = repository is null ? null : Base(repository, gaps); }
        catch (GitFailure failure) { gaps.Add(new PreflightGap.Git(failure.Problem, failure.Message)); }
        RunId? active = null;
        try { active = Active(workflow.Id)?.Id; }
        catch (IOException error) { gaps.Add(new PreflightGap.Records(error.Message)); }
        // A node's run carries the current results of earlier runs for the tasks before it that it does not run (#90).
        ImmutableArray<PreflightCarried> carried = [];
        if (node is { } started && workflow.Tasks.ContainsKey(started))
        {
            try { carried = Carried(revision, found, started); }
            catch (IOException error) { gaps.Add(new PreflightGap.Records(error.Message)); }
            if (RunScope.Predecessors(workflow)[started].Order()
                .Where(predecessor => !carried.Any(item => item.Task == predecessor && item.Refusal is null)).ToImmutableArray() is { IsEmpty: false } missing)
                gaps.Add(new PreflightGap.After(started, missing));
        }
        var preview = new RunPreflight(_project, revision, git, found, tasks, active) { Gaps = gaps.ToImmutable(), Node = node, Carried = carried };
        // A node's Run offers only that node's earlier report: including another task's would hand on a result that no
        // task after it asked for.
        return found is null ? preview : preview with
        {
            Reusable = [.. Reusable(workflow, found).Where(report => node is null || report.Task == node)],
            Planners = [.. Planners(workflow, found).Where(planner => node is null || planner.Task == node)],
        };
    }

    /// <summary>The run a preview carries results into, which names nothing it writes.</summary>
    private static readonly RunId PreviewRun = new(Guid.Parse("00000000-0000-0000-0000-00000000cccc"));

    /// <summary>
    /// The results of earlier runs a run of <paramref name="node"/> would carry as their reports and artifacts, and the needed
    /// ones it could not carry and why (#90). Throws <see cref="IOException"/> when a run record cannot be read.
    /// </summary>
    private ImmutableArray<PreflightCarried> Carried(ApprovedRevision revision, PreflightBase? found, TaskId node)
    {
        if (found is null) return [];
        var history = RunHistory.Of(_store.Records(revision.Snapshot.Id), revision.Snapshot);
        var build = Carrying.Build(Seed(PreviewRun, revision, new(found.Head), node), history, PreviewConfirmation);
        var rows = ImmutableArray.CreateBuilder<PreflightCarried>();
        foreach (var task in build.Carried.Select(item => item.Result.Task).Concat(build.Refused.Keys).Distinct().Order())
        {
            var source = history.CurrentOf(task)!;
            rows.Add(new(task, source.Run.Id, source.Result.Id, build.Refused.GetValueOrDefault(task)));
        }
        return rows.ToImmutable();
    }

    /// <summary>A run of <paramref name="node"/> on <paramref name="codeBase"/> as its approval begins, before it carries anything.</summary>
    private static RunRecord Seed(RunId run, ApprovedRevision revision, RunBase codeBase, TaskId node) =>
        new(run, revision.Snapshot.Id, codeBase, revision) { Requested = [node] };

    /// <summary>Why the task could not start as configured, as a single start would say, or null.</summary>
    private StartProblem? Gap(TaskDefinition task) => Gap(task, _project, _clients, _skills);

    /// <summary>Why <paramref name="task"/> could not start in a run as configured, as the preflight names it, or null.</summary>
    internal static StartProblem? Gap(TaskDefinition task, string project, IReadOnlyDictionary<ClientId, ClientStatus> clients,
        Func<ExecutionSettings?, SkillListing?>? skills = null)
    {
        if (task.Blueprint.Work is WorkSpec.Person) return null;
        // A run renders the prompt from the node and its inputs, so this checks the agent and its settings, then the fields.
        if (StartCheck.Evaluate(task, project, clients, new Resumption(null, "Preflight"), skills: skills?.Invoke(task.Execution)) is StartVerdict.Blocked blocked)
            return blocked.Problem;
        return task.Blueprint.Fields.FirstOrDefault(field => field.Required && string.IsNullOrWhiteSpace(task.Field(field.Key))) is { } missing
            ? new StartProblem.FieldMissing(missing.Label) : null;
    }

    private PreflightBase? Base(GitRepository repository, ImmutableArray<PreflightGap>.Builder gaps)
    {
        if (Value(repository.ResolveCommit("HEAD")) is not { } head)
        {
            gaps.Add(new PreflightGap.NoCommit());
            return null;
        }
        var branch = Value(repository.SymbolicHead(_project));
        // A run's checkouts are ignored once it made one, and naming an ignored folder fails git add.
        string[] excluded = repository.CheckIgnore(_project, ".worktrees/").ExitCode == 0 ? [".idp"] : Excluded;
        var tree = Value(GitRepository.Snapshot(_project, excluded, _environment, GitLimits.Default));
        var headTree = Value(repository.ReadCommit(head)).Tree;
        ImmutableArray<string> changed = [.. Value(repository.DiffTreePaths(headTree, tree)).Where(path => !InData(path))];
        ImmutableArray<string> ignored = [.. Value(repository.IgnoredEntries(_project)).Where(path => !InData(path.TrimEnd('/'))).Order(StringComparer.Ordinal)];
        ImmutableArray<PreflightSubmodule> submodules = [];
        switch (repository.SubmoduleStatus(_project))
        {
            case GitRead<string>.Read status:
                submodules = [.. status.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Submodule)];
                break;
            case GitRead<string>.Failed failed:
                gaps.Add(new PreflightGap.Submodules(failed.Detail));
                break;
        }
        return new(head, branch, changed, ignored, submodules);
    }

    /// <summary>One line of <c>git submodule status</c>: a state character, the commit, the path, and a description.</summary>
    private static PreflightSubmodule Submodule(string line)
    {
        var rest = line[1..];
        var space = rest.IndexOf(' ');
        var path = rest[(space + 1)..];
        if (path.EndsWith(')') && path.LastIndexOf(" (", StringComparison.Ordinal) is var description and > 0) path = path[..description];
        return new(path, new(rest[..space]), line[0] switch
        {
            '-' => SubmoduleState.Uninitialized,
            '+' => SubmoduleState.Changed,
            'U' => SubmoduleState.Conflicted,
            _ => SubmoduleState.Current,
        });
    }

    /// <summary>The newest standalone report of each task that E1's validator would let the run reuse from HEAD.</summary>
    private ImmutableArray<PreflightReport> Reusable(Workflow workflow, PreflightBase found)
    {
        var reports = ImmutableArray.CreateBuilder<PreflightReport>();
        foreach (var task in workflow.Tasks.Values.OrderBy(task => task.Id))
        {
            if (_latest.GetValueOrDefault(task.Id) is not { Status: AttemptStatus.Succeeded } attempt ||
                workflow.Connections.Keys.Any(edge => edge.To == task.Id)) continue;
            var source = new AttemptSource.Standalone(task.Id, attempt.Id);
            if (ReportReuse.Validate(_project, task, source, found.Head, PreviewConfirmation) is { Rejection: null, Report: { } report })
                reports.Add(new(task.Id, attempt.Id, report));
        }
        return reports.ToImmutable();
    }

    /// <summary>
    /// Each read-only root planner's newest standalone attempt, and why the run could not include its report from HEAD, if
    /// it could not. A planner that waits for the person counts as finished, which a confirmation including it makes it.
    /// </summary>
    private ImmutableArray<PreflightPlanner> Planners(Workflow workflow, PreflightBase found)
    {
        var planners = ImmutableArray.CreateBuilder<PreflightPlanner>();
        foreach (var task in workflow.Tasks.Values.OrderBy(task => task.Id))
        {
            if (task.Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.ReadOnly, Proposes: true } ||
                workflow.Connections.Keys.Any(edge => edge.To == task.Id) || _latest.GetValueOrDefault(task.Id) is not { } attempt) continue;
            var source = new AttemptSource.Standalone(task.Id, attempt.Id);
            var check = ReportReuse.ValidatePlanner(_project, workflow, task.Id, source, attempt.Turns.Count, found.Head, PreviewConfirmation, waiting: true);
            planners.Add(new(task.Id, attempt.Id, attempt.Turns.Count, attempt.Status, attempt.Turns[^1].FinalText, check.Rejection?.Problem));
        }
        return planners.ToImmutable();
    }

    private static readonly OperationId PreviewConfirmation = new(Guid.Parse("00000000-0000-0000-0000-00000000aaaa"));

    /// <summary>
    /// Why <paramref name="inclusions"/> cannot be approved: one task twice, or a report <paramref name="live"/> does not
    /// offer. A report that the confirmed preview offered and the live one does not makes the preview stale instead.
    /// </summary>
    private static RunApproval? InclusionProblem(RunPreflight shown, RunPreflight live, ImmutableArray<ReportInclusion> inclusions)
    {
        if (inclusions.Select(inclusion => inclusion.Task).Distinct().Count() != inclusions.Length)
            return new RunApproval.Refused(ApprovalProblem.NotConfirmable, "A task is included twice.") { Current = live };
        foreach (var inclusion in inclusions)
        {
            if (Offers(live, inclusion) is null) continue;
            if (Offers(shown, inclusion) is null) return new RunApproval.Changed(live);
            return new RunApproval.Refused(ApprovalProblem.InclusionRefused, (Offers(live, inclusion) ?? RunProblem.ReuseUnverifiable).ToString())
            { Current = live };
        }
        return null;
    }

    /// <summary>Null when <paramref name="preview"/> offers <paramref name="inclusion"/>, else why not.</summary>
    private static RunProblem? Offers(RunPreflight preview, ReportInclusion inclusion)
    {
        if (preview.Planners.FirstOrDefault(planner => planner.Task == inclusion.Task) is { } planner)
            return planner.Source == inclusion.Source && planner.Turn == inclusion.Turn && planner.Problem is null ? null
                : planner.Problem ?? RunProblem.ReuseUnverifiable;
        return preview.Reusable.Any(report => report.Task == inclusion.Task && report.Source == inclusion.Source && inclusion.Turn == 1)
            ? null : RunProblem.ReuseUnverifiable;
    }

    /// <summary>
    /// Marks each included planner that waits for the person done at the turn the person saw. One that no longer waits
    /// there is left as it is, and the approval's validation decides.
    /// </summary>
    private RunApproval? Finish(RunPreflight live, ImmutableArray<ReportInclusion> inclusions)
    {
        var finished = false;
        foreach (var inclusion in inclusions)
        {
            if (live.Planners.FirstOrDefault(planner => planner.Task == inclusion.Task) is not { Status: AttemptStatus.WaitingForInput }) continue;
            if (_finish?.Invoke(inclusion.Task, new(inclusion.Source, inclusion.Turn)) is { } problem)
                return new RunApproval.Refused(ApprovalProblem.InclusionRefused, problem.GetType().Name) { Current = live };
            finished = true;
        }
        if (finished) _probe?.Invoke("approval.finished.after");
        return null;
    }

    /// <summary>
    /// Approves exactly one run for <paramref name="confirmation"/>, against <paramref name="current"/>, the workflow as the
    /// document holds it now. The run starts from the current branch's HEAD. Under the workflow's approval lock it records
    /// an <see cref="ApprovalIntent"/> before the base pin and the journal, so a confirmation repeated after a crash, or a
    /// new one of the same content, finishes the same run.
    /// </summary>
    public RunApproval Approve(Workflow current, RunConfirmation confirmation)
    {
        if (confirmation.Command.Value == Guid.Empty)
            return new RunApproval.Refused(ApprovalProblem.ConfirmationRequired, "A confirmation needs its own command ID.");
        var live = Inspect(current, confirmation.Preview.Node);
        var intents = new ApprovalIntents(_project, live.Workflow);
        try
        {
            using var held = intents.Lock();
            if (held is null) return new RunApproval.Refused(ApprovalProblem.ApprovalBusy, "Another confirmation of this workflow is still running.");
            _probe?.Invoke("approval.locked");
            var all = intents.All();
            var repository = GitRepository.Open(_project, _environment) is RepositoryOpen.Opened opened ? opened.Repository : null;
            if (repository is not null) ReleasePins(repository, all);
            var own = all.FirstOrDefault(intent => intent.Confirmations.Contains(confirmation.Command));
            if (own is not null && Approved(live.Workflow, own.Run) is { } ownRun)
                return new RunApproval.Approved(own.Run, ownRun.Base, true);
            if (!Current(confirmation.Preview, live) || !SameCarried(confirmation.Preview, live)) return new RunApproval.Changed(live);
            if (!live.Confirmable || repository is null)
                return new RunApproval.Refused(ApprovalProblem.NotConfirmable, "The preview has gaps or its node cannot start.") { Current = live };
            ImmutableArray<ReportInclusion> inclusions = [.. confirmation.Include.OrderBy(inclusion => inclusion.Task)];
            if (InclusionProblem(confirmation.Preview, live, inclusions) is { } refusal) return refusal;
            if (Active(live.Workflow) is { } active)
            {
                if (all.FirstOrDefault(intent => intent.Run == active.Id) is not { } approving || !approving.Matches(live, inclusions))
                    return new RunApproval.Busy(active.Id);
                intents.Write(Confirmed(approving, confirmation.Command));
                return new RunApproval.Approved(active.Id, active.Base, true);
            }
            var pending = all.Where(intent => Approved(live.Workflow, intent.Run) is null).ToArray();
            var adopted = pending.FirstOrDefault(intent => intent.Run == own?.Run && intent.Matches(live, inclusions)) ??
                pending.FirstOrDefault(intent => intent.Matches(live, inclusions));
            foreach (var stale in pending.Where(intent => intent.Run != adopted?.Run)) intents.Remove(stale.Run, repository);
            var chosen = adopted is null
                ? new ApprovalIntent(2, new(OperationIds.Derive(confirmation.Command, "run").Value), OperationIds.Derive(confirmation.Command, "approve"),
                    [confirmation.Command], live.Revision, live.Base!.Head) { Inclusions = inclusions, Node = live.Node }
                : Confirmed(adopted, confirmation.Command);
            intents.Write(chosen);
            _probe?.Invoke("approval.intent.after");
            if (Finish(live, chosen.Inclusions) is { } unfinished) return unfinished;
            // The run's finished work merges into the branch HEAD names now, or nowhere from a detached HEAD (#148).
            var codeBase = new RunBase(chosen.Head) { Branch = live.Base!.Branch };
            // Keep the base before the journal names it, so garbage collection cannot remove it before its first preparation.
            if (!ApprovalPin.Hold(repository, chosen.Run, codeBase.Commit))
                return new RunApproval.Refused(ApprovalProblem.GitFailed, $"Git could not keep the run base under {ApprovalPin.Name(chosen.Run)}.");
            _probe?.Invoke("approval.pinned.after");
            // The results the preview showed carried bring their reports and artifacts, copied before the journal names them.
            var carried = CarryBuild.Empty;
            if (chosen.Node is { } node)
            {
                var history = RunHistory.Of(_store.Records(live.Workflow), chosen.Revision.Snapshot);
                carried = Carrying.Build(Seed(chosen.Run, chosen.Revision, codeBase, node), history, chosen.Operation);
                _probe?.Invoke("approval.carry.built");
                if (Carrying.Copy(_project, chosen.Run, live.Workflow, carried, history) is { } failure)
                    return new RunApproval.Refused(ApprovalProblem.StorageUnavailable, failure);
                _probe?.Invoke("approval.carry.kept");
            }
            var decision = _store.Approve(live.Workflow, chosen.Run, chosen.Operation, chosen.Revision, codeBase, chosen.Inclusions,
                chosen.Confirmations[0], chosen.Node, carried.Carried);
            _probe?.Invoke("approval.approved.after");
            return decision switch
            {
                RunDecision.Created created => new RunApproval.Approved(chosen.Run, created.Record.Base, false),
                RunDecision.Existing existing => new RunApproval.Approved(chosen.Run, existing.Record.Base, true),
                RunDecision.Rejected { Reason.Problem: RunProblem.RunBusy } when Active(live.Workflow) is { } other => new RunApproval.Busy(other.Id),
                RunDecision.Rejected { Reason.Task: not null } inclusion when !chosen.Inclusions.IsEmpty =>
                    new RunApproval.Refused(ApprovalProblem.InclusionRefused, inclusion.Reason.Problem.ToString()) { Current = live },
                RunDecision.Rejected rejected => new RunApproval.Refused(ApprovalProblem.StorageUnavailable, rejected.Reason.Problem.ToString()),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new RunApproval.Refused(ApprovalProblem.StorageUnavailable, error.Message);
        }
    }

    /// <summary>
    /// Removes the pin of each approved run whose own base ref now holds its base, and of each run that settled before its
    /// first preparation, which never writes a base ref (#85).
    /// </summary>
    private void ReleasePins(GitRepository repository, ImmutableArray<ApprovalIntent> all)
    {
        foreach (var intent in all)
        {
            switch (Approved(intent.Revision.Snapshot.Id, intent.Run))
            {
                case { RunKey: { } key } record:
                    ApprovalPin.Release(repository, intent.Run, record.Base.Commit, RunLayout.ApprovedBase(key));
                    break;
                case { Phase: RunPhase.Completed or RunPhase.Stopped or RunPhase.Failed or RunPhase.Abandoned }:
                    ApprovalPin.Release(repository, intent.Run);
                    break;
            }
        }
    }

    private static ApprovalIntent Confirmed(ApprovalIntent intent, OperationId command) =>
        intent.Confirmations.Contains(command) ? intent : intent with { Confirmations = intent.Confirmations.Add(command) };

    /// <summary>
    /// Whether <paramref name="preview"/> still shows what the confirmation approves: the same execution snapshot and HEAD,
    /// and a project that was not clean when the preview said so.
    /// </summary>
    private static bool Current(RunPreflight preview, RunPreflight live) =>
        preview.Workflow == live.Workflow && preview.Revision.Id == live.Revision.Id && (preview.Base, live.Base) switch
        {
            (null, null) => true,
            ({ } shown, { } found) => shown.Head == found.Head && (!shown.Changed.IsEmpty || found.Changed.IsEmpty),
            _ => false,
        };

    /// <summary>Whether <paramref name="preview"/> carries what <paramref name="live"/> does: the same tasks, from the same results.</summary>
    private static bool SameCarried(RunPreflight preview, RunPreflight live) =>
        preview.Used.Select(row => (row.Task, row.Run, row.Result)).SequenceEqual(live.Used.Select(row => (row.Task, row.Run, row.Result)));

    /// <summary>The run's record once its journal holds the approval, else null.</summary>
    private RunRecord? Approved(WorkflowId workflow, RunId run) => _store.Read(workflow, run) switch
    {
        RunRead.Loaded loaded => loaded.Record,
        RunRead.Rejected { Reason.Problem: RunProblem.NotApproved or RunProblem.EarlierFormat } => null,
        RunRead.Rejected { Reason.Problem: RunProblem.IncompleteTail, Prefix: null } => null,
        var other => throw new IOException($"Run {run}: {((RunRead.Rejected)other).Reason.Problem}."),
    };

    /// <summary>The workflow's run that is approved or stopping, if any. Throws <see cref="IOException"/> for a run it cannot read.</summary>
    public RunId? ActiveRun(WorkflowId workflow) => Active(workflow)?.Id;

    /// <summary>The workflow's run that is approved or stopping, if any.</summary>
    private RunRecord? Active(WorkflowId workflow)
    {
        var folder = Path.Combine(Projects.DataFolder.Runs(_project), workflow.ToString());
        if (!Directory.Exists(folder)) return null;
        foreach (var run in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            if (!Guid.TryParse(Path.GetFileName(run), out var id) || !File.Exists(Path.Combine(run, "events.jsonl"))) continue;
            if (Approved(workflow, new(id)) is { Phase: RunPhase.Approved or RunPhase.StopRequested } record) return record;
        }
        return null;
    }

    private static PreflightTask Task(Workflow workflow, TaskDefinition task)
    {
        ImmutableArray<PreflightInput> inputs = [.. workflow.Connections.Where(pair => pair.Key.To == task.Id)
            .OrderBy(pair => pair.Key.From).Select(pair => new PreflightInput(pair.Key.From, pair.Value))];
        var agent = task.Blueprint.Work as WorkSpec.Agent;
        return new(task.Id, task.Title, task.Blueprint.Work switch
            {
                WorkSpec.Agent => WorkKind.Agent,
                WorkSpec.Review => WorkKind.Review,
                WorkSpec.Merge => WorkKind.Merge,
                _ => WorkKind.Person,
            }, agent?.Access, agent?.Proposes ?? false, task.Execution, task.Conversation, inputs,
            !inputs.Any(input => input.Kind == ConnectionKind.Dependency));
    }

    private static bool InData(string path) => Excluded.Any(folder => path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal));

    private static T Value<T>(GitRead<T> read) => read is GitRead<T>.Read value ? value.Value
        : throw new GitFailure(((GitRead<T>.Failed)read).Problem, ((GitRead<T>.Failed)read).Detail);

    private sealed class GitFailure(MaterializationProblem problem, string detail) : Exception(detail)
    {
        public MaterializationProblem Problem { get; } = problem;
    }
}

using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Run Workflow's preflight and its approval. <see cref="Inspect"/> only reads.
/// </summary>
internal sealed class RunApprovals
{
    private static readonly string[] Excluded = [".idp", ".worktrees"];

    private readonly string _project;
    private readonly RunStore _store;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly IReadOnlyDictionary<ClientId, ClientStatus> _clients;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _probe;
    private readonly IReadOnlyDictionary<TaskId, AttemptRecord> _latest;

    /// <param name="latest">The newest standalone attempt of each task, whose report the preview may list as reusable.</param>
    public RunApprovals(string project, RunStore store, IReadOnlyDictionary<string, string>? environment,
        IReadOnlyDictionary<ClientId, ClientStatus> clients, IReadOnlyDictionary<TaskId, AttemptRecord> latest, TimeProvider clock,
        Action<string>? probe = null)
    {
        _probe = probe;
        _latest = latest;
        _project = Path.GetFullPath(project);
        _store = store;
        _environment = environment ?? new Dictionary<string, string>();
        _clients = clients;
        _clock = clock;
    }

    public RunPreflight Inspect(Workflow workflow)
    {
        var revision = Revision.Capture(workflow);
        ImmutableArray<PreflightTask> tasks = [.. workflow.Tasks.Values.OrderBy(task => task.Id).Select(task => Task(workflow, task))];
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
        if (repository is not null && (GitRepository.ParseVersion(repository.Version) is not { } version || version < new Version(2, 43, 0)))
        {
            foreach (var task in tasks.Where(task => task.Inputs.Count(input => input.Kind == ConnectionKind.Dependency) > 1))
                gaps.Add(new PreflightGap.Join(task.Task, $"Joins need Git 2.43 or later. Installed: {repository.Version}."));
        }
        var found = repository is null ? null : Base(repository, gaps);
        var preview = new RunPreflight(_project, revision, git, found, tasks, Active(workflow.Id)?.Id) { Gaps = gaps.ToImmutable() };
        return found is null ? preview : preview with { Reusable = Reusable(workflow, found, RunPreflight.Offered(found)) };
    }

    /// <summary>Why the task could not start as configured, as a single start would say, or null.</summary>
    private StartProblem? Gap(TaskDefinition task)
    {
        if (task.Blueprint.Work is WorkSpec.Person) return null;
        // A run renders the prompt from the node and its inputs, so this checks the agent and its settings, then the fields.
        if (StartCheck.Evaluate(task, _project, _clients, new Resumption(null, "Preflight")) is StartVerdict.Blocked blocked) return blocked.Problem;
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
        ImmutableArray<string> unmerged = [.. Value(repository.UnmergedEntries(_project)).Select(entry => entry.Path).Distinct().Order(StringComparer.Ordinal)];
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
        return new(head, branch, tree, changed, ignored, unmerged, submodules);
    }

    /// <summary>One line of <c>git submodule status</c>: a state character, the commit, the path, and a description.</summary>
    private static PreflightSubmodule Submodule(string line)
    {
        var fields = line[1..].Split(' ', 3);
        return new(fields[1], new(fields[0]), line[0] switch
        {
            '-' => SubmoduleState.Uninitialized,
            '+' => SubmoduleState.Changed,
            'U' => SubmoduleState.Conflicted,
            _ => SubmoduleState.Current,
        });
    }

    /// <summary>
    /// The newest standalone report of each task that E1's validator would let the run reuse, for each base it matches. A
    /// snapshot is compared through its tree, because its commit is made only at approval.
    /// </summary>
    private ImmutableArray<PreflightReport> Reusable(Workflow workflow, PreflightBase found, ImmutableArray<BaseChoice> choices)
    {
        var confirmation = new OperationId(Guid.Parse("00000000-0000-0000-0000-00000000aaaa"));
        var reports = ImmutableArray.CreateBuilder<PreflightReport>();
        foreach (var task in workflow.Tasks.Values.OrderBy(task => task.Id))
        {
            if (_latest.GetValueOrDefault(task.Id) is not { Status: AttemptStatus.Succeeded } attempt ||
                workflow.Connections.Keys.Any(edge => edge.To == task.Id)) continue;
            var source = new AttemptSource.Standalone(task.Id, attempt.Id);
            string? report = null;
            var bases = ImmutableArray.CreateBuilder<BaseChoice>();
            foreach (var choice in choices)
            {
                var commit = choice == BaseChoice.Head ? found.Head : new CommitId(found.WorkTree.Hex);
                if (ReportReuse.Validate(_project, task, source, commit, confirmation) is { Rejection: null } reuse)
                {
                    report = reuse.Report;
                    bases.Add(choice);
                }
            }
            if (report is not null) reports.Add(new(task.Id, attempt.Id, report, bases.ToImmutable()));
        }
        return reports.ToImmutable();
    }

    /// <summary>
    /// Approves exactly one run for <paramref name="confirmation"/>, against <paramref name="current"/>, the workflow as the
    /// document holds it now. Under the workflow's approval lock it records an <see cref="ApprovalIntent"/> before the
    /// snapshot commit and the journal, so a confirmation repeated after a crash, or a new one of the same content,
    /// finishes the same run.
    /// </summary>
    public RunApproval Approve(Workflow current, RunConfirmation confirmation)
    {
        var live = Inspect(current);
        var choice = confirmation.Choice;
        var intents = new ApprovalIntents(_project, live.Workflow);
        try
        {
            using var held = intents.Lock();
            if (held is null) return new RunApproval.Refused(ApprovalProblem.ApprovalBusy, "Another confirmation of this workflow is still running.");
            _probe?.Invoke("approval.locked");
            var all = intents.All();
            var own = all.FirstOrDefault(intent => intent.Confirmations.Contains(confirmation.Command));
            if (own is not null && Approved(live.Workflow, own.Run) is { } ownRun)
                return new RunApproval.Approved(own.Run, ownRun.Base, true);
            if (!Current(confirmation.Preview, live, choice)) return new RunApproval.Changed(live);
            if (!live.Gaps.IsEmpty || !live.Choices.Contains(choice))
                return new RunApproval.Refused(ApprovalProblem.NotConfirmable, "The preview has gaps or does not offer this base.");
            if (Active(live.Workflow) is { } active)
            {
                if (all.FirstOrDefault(intent => intent.Run == active.Id) is not { } approving || !approving.Matches(live, choice))
                    return new RunApproval.Busy(active.Id);
                intents.Write(Confirmed(approving, confirmation.Command));
                return new RunApproval.Approved(active.Id, active.Base, true);
            }
            var pending = all.Where(intent => Approved(live.Workflow, intent.Run) is null).ToArray();
            var adopted = pending.FirstOrDefault(intent => intent == own && intent.Matches(live, choice)) ??
                pending.FirstOrDefault(intent => intent.Matches(live, choice));
            foreach (var stale in pending.Where(intent => intent != adopted)) intents.Remove(stale.Run);
            var chosen = adopted is null
                ? new ApprovalIntent(1, new(OperationIds.Derive(confirmation.Command, "run").Value), OperationIds.Derive(confirmation.Command, "approve"),
                    [confirmation.Command], live.Revision, choice, live.Base!.Head, choice == BaseChoice.Snapshot ? live.Base.WorkTree : null,
                    DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().ToUnixTimeSeconds()))
                : Confirmed(adopted, confirmation.Command);
            intents.Write(chosen);
            _probe?.Invoke("approval.intent.after");
            var codeBase = new RunBase(chosen.Head, BaseChoice.Head);
            if (choice == BaseChoice.Snapshot)
            {
                if (GitRepository.Open(_project, _environment) is not RepositoryOpen.Opened opened ||
                    opened.Repository.CreateCommit(chosen.SnapshotRecipe()) is not GitRead<CommitId>.Read commit)
                    return new RunApproval.Refused(ApprovalProblem.GitFailed, "Git could not record the snapshot of the uncommitted work.");
                codeBase = new RunBase(commit.Value, BaseChoice.Snapshot);
                _probe?.Invoke("approval.snapshot.after");
            }
            var decision = _store.Approve(live.Workflow, chosen.Run, chosen.Operation, chosen.Revision, codeBase);
            _probe?.Invoke("approval.approved.after");
            return decision switch
            {
                RunDecision.Created => new RunApproval.Approved(chosen.Run, codeBase, false),
                RunDecision.Existing => new RunApproval.Approved(chosen.Run, codeBase, true),
                RunDecision.Rejected { Reason.Problem: RunProblem.RunBusy } when Active(live.Workflow) is { } other => new RunApproval.Busy(other.Id),
                RunDecision.Rejected rejected => new RunApproval.Refused(ApprovalProblem.StorageUnavailable, rejected.Reason.Problem.ToString()),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new RunApproval.Refused(ApprovalProblem.StorageUnavailable, error.Message);
        }
    }

    private static ApprovalIntent Confirmed(ApprovalIntent intent, OperationId command) =>
        intent.Confirmations.Contains(command) ? intent : intent with { Confirmations = intent.Confirmations.Add(command) };

    /// <summary>
    /// Whether <paramref name="preview"/> still shows what <paramref name="choice"/> approves: the same execution snapshot and
    /// HEAD, a project that was not clean when the preview said so, and for a snapshot the same work tree.
    /// </summary>
    private static bool Current(RunPreflight preview, RunPreflight live, BaseChoice choice) =>
        preview.Workflow == live.Workflow && preview.Revision.Id == live.Revision.Id && (preview.Base, live.Base) switch
        {
            (null, null) => true,
            ({ } shown, { } found) => shown.Head == found.Head && (!shown.Changed.IsEmpty || found.Changed.IsEmpty) &&
                (choice == BaseChoice.Head || shown.WorkTree == found.WorkTree),
            _ => false,
        };

    /// <summary>The run's record once its journal holds the approval, else null.</summary>
    private RunRecord? Approved(WorkflowId workflow, RunId run) => _store.Read(workflow, run) switch
    {
        RunRead.Loaded loaded => loaded.Record,
        RunRead.Rejected { Reason.Problem: RunProblem.NotApproved } => null,
        RunRead.Rejected { Reason.Problem: RunProblem.IncompleteTail, Prefix: null } => null,
        var other => throw new IOException($"Run {run}: {((RunRead.Rejected)other).Reason.Problem}."),
    };

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
                _ => WorkKind.Person,
            }, agent?.Access, agent?.Proposes ?? false, task.Execution, task.Conversation, inputs,
            !inputs.Any(input => input.Kind == ConnectionKind.Dependency));
    }

    private static bool InData(string path) => Excluded.Any(folder => path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal));

    private static T Value<T>(GitRead<T> read) => read is GitRead<T>.Read value ? value.Value : throw new InvalidOperationException(((GitRead<T>.Failed)read).Detail);
}

using System.Collections.Immutable;
using IDevelop.Core.Tests.Git;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

internal sealed class PreparationFixture : IDisposable
{
    public readonly GitFixture Git = new();
    public readonly RunStore Store;
    public readonly RunId RunId;
    private int _id = 100;
    private int _op = 1000;
    public readonly CommitId A;
    public PreparationFixture(Workflow workflow, CommitId? runBase = null, RunId? run = null)
    {
        A = Git.Diamond();
        RunId = run ?? Run;
        Store = RunStore.Open(Git.Folder, new Clock(), () => Id(++_id));
        Assert.IsType<RunDecision.Created>(Store.Approve(W, RunId, Op(), Revision.Capture(workflow), new(runBase ?? A, BaseChoice.Head)));
    }

    public Materializer Materializer(IJoinComposer? joins = null, Action<string>? probe = null, string? project = null) =>
        Execution.Materializer.Open(project ?? Git.Folder, Store, joins, new UnprovenBoundary(), new Clock(), Git.Environment, probe);
    public OperationId Op() => new(Id(++_op));
    public RunRecord Read() => Assert.IsType<RunRead.Loaded>(Store.Read(W, RunId)).Record;
    public ValueTask<Preparation> Prepare(TaskId task, OperationId? operation = null, AttemptCause? cause = null, string? prompt = null) =>
        Materializer().Prepare(W, RunId, operation ?? Op(), task, cause ?? new AttemptCause.Initial(), prompt);

    public static TaskDefinition Writer(TaskId task, string template = "{{brief}}")
    {
        var blueprint = new Blueprint(new($"example.writer.{task}", 1), "Writer",
            new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse(template)),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous));
        return new(task, blueprint) { Execution = Task().Execution, Title = task == T ? "B" : task == C ? "C" : "U" };
    }

    public async Task<ResultRecord> Publish(TaskId task, CommitId commit, string report = "B ready.\n", bool artifact = false, string artifactName = "payload")
    {
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(task));
        var execution = ready.Execution;
        var attempt = Read().Attempts[execution.Launch.Attempt];
        var input = Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(Store.Claim(W, RunId, Op(), execution.Launch, input, execution.PromptHash));
        var definition = Read().Revision.Snapshot.Tasks[task];
        var folder = Store.AttemptFolder(W, RunId, task, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(
            At, attempt.Id, task, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
        { RunBinding = new(W, RunId, attempt.Revision, input.Id), Conversation = definition.Conversation, ReadOnly = false }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("fixture")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded(report)));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        Assert.IsType<RunDecision.Recorded>(Store.CloseAttempt(W, RunId, Op(), attempt.Id, TerminalAttemptOutcome.Succeeded, Checkpoint(folder)));
        var details = GitFixture.Read(Git.Open().ReadCommit(commit));
        var result = new ResultId(Id(++_id));
        ImmutableArray<ArtifactRecord> artifacts = [];
        if (artifact)
        {
            var relative = $"results/{result.Value:D}/artifacts/{artifactName}";
            var path = Path.Combine(new RunStorage(Git.Folder, W, RunId).Folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [67, 0, 127]);
            artifacts = [new(artifactName, relative, new("134f4812acb8aa0b274fd71f834f9d36a21ab174e3fbab2f7956eac4b0a469c7"), 3)];
        }
        var publication = new MaterializationPlan.Publication(attempt.Id, result, null, details.Parents[0], null,
            new(details.Tree, details.Parents, "a\n", "E2 <e2@example.test>", "E2 <e2@example.test>", At), commit, report, artifacts);
        var plan = Op();
        Assert.IsType<RunDecision.Recorded>(Store.Record(W, RunId, plan, new RunEvent.Planned(publication)));
        // The fixture commit exists independently; publication receipts still describe real ref transitions.
        Git.Git("update-ref", execution.Location.Owner.Branch, details.Parents[0].Hex);
        Observe(plan, new(execution.Location.Owner.Branch, details.Parents[0], commit));
        var record = Read();
        Observe(plan, new($"refs/idp/{record.RunKey}/result/{record.TaskKeys[task]}/{attempt.Id.Value:D}", null, commit));
        return Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(Store.AcceptPublication(W, RunId, Op(), plan)).Event).Result;
    }

    public void Observe(OperationId plan, RefChange change)
    {
        var intent = Op();
        Assert.IsType<RunDecision.Recorded>(Store.Record(W, RunId, intent, new RunEvent.GitIntended(plan, new GitMutation.MoveRef(change))));
        Assert.IsType<RefMove.Moved>(Git.Open().MoveRef(change));
        Assert.IsType<RunDecision.Recorded>(Store.Record(W, RunId, Op(), new RunEvent.GitObserved(intent, new(false, change.Target.Hex))));
    }

    public void Dispose() => Git.Dispose();
    public sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }
}

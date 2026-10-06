using System.Collections.Immutable;
using System.Security.Cryptography;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests.Runs;

internal sealed class RunFixtures : IDisposable
{
    public const string V1 = "4e59fcd81eea359ae4d1680bef9cb518a5b9d7a1a9b49e0b936062f90b4d3d7a";

    public const string V2 = "fa87b483a3bf3006501c11c26162f95b8729c71a8a0850ae710467a53399fcca";

    public const string Canonical =
        "{\"connections\":[],\"schema\":1,\"tasks\":[{\"blueprint\":\"example.agent@1\",\"conversation\":\"autonomous\"," +
        "\"execution\":{\"client\":\"codex\",\"model\":\"m1\",\"reasoning\":\"high\"},\"fields\":[{\"key\":\"brief\"," +
        "\"required\":true,\"value\":\"Inspect\"}],\"id\":\"00000000-0000-0000-0000-000000000002\",\"title\":\"Plan\"," +
        "\"work\":{\"access\":\"readOnly\",\"kind\":\"agent\",\"proposes\":false,\"template\":\"{{brief}}\"}}]," +
        "\"workflow\":\"00000000-0000-0000-0000-000000000001\"}";

    public static readonly WorkflowId W = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    public static readonly TaskId T = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));

    public static readonly TaskId U = new(Guid.Parse("00000000-0000-0000-0000-000000000003"));

    public static readonly TaskId C = new(Guid.Parse("00000000-0000-0000-0000-000000000004"));

    public static readonly TaskId D = new(Guid.Parse("00000000-0000-0000-0000-000000000005"));

    public static readonly RunId Run = new(Guid.Parse("00000000-0000-0000-0000-000000000010"));

    public static readonly RunId OtherRun = new(Guid.Parse("00000000-0000-0000-0000-000000000011"));

    public static readonly AttemptId A1 = new(Guid.Parse("00000000-0000-0000-0000-000000000102"));

    public static readonly CommitId Base = new("1111111111111111111111111111111111111111");

    public static readonly Digest Prompt = new("e0723a86a5b9408aee9113031785d3d15d9702892f31a46e5fe927c0c8552675");

    public static readonly DateTimeOffset At = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new(Path.Combine(AppContext.BaseDirectory, "run-test-data", Guid.NewGuid().ToString("N")));

    private int _id = 100;

    private int _operation = 1000;

    public string Project
    {
        get;
    }

    public RunStore Store
    {
        get;
    }

    public Workflow Workflow
    {
        get; private set;
    }

    public RunFixtures(Workflow? workflow = null)
    {
        Project = _temp.Create("project");
        Store = NewStore();
        Workflow = workflow ?? FixtureWorkflow();
    }

    public RunStore NewStore() => RunStore.Open(Project, new FixedClock(), () => Id(++_id));

    public string AnotherProject() => _temp.Create("other");

    public OperationId Op() => new(Id(++_operation));

    public static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}");

    public void Dispose() => _temp.Dispose();

    public RunRecord Read(RunId? run = null) => Assert.IsType<RunRead.Loaded>(Store.Read(W, run ?? Run)).Record;

    public void Approve(CommitId? codeBase = null, RunId? run = null) => Assert.IsType<RunDecision.Created>(
        Store.Approve(W, run ?? Run, Op(), Revision.Capture(Workflow), new(codeBase ?? Base, BaseChoice.Head)));

    public RunEvent.Reserved Reserve(TaskId? task = null, AttemptCause? cause = null, RunId? run = null, CommitId? codeBase = null) =>
        Assert.IsType<RunEvent.Reserved>(Assert.IsType<RunDecision.Created>(Store.Reserve(W, run ?? Run, Op(), task ?? T,
            Read(run).Revision.Id, cause ?? new AttemptCause.Initial(), codeBase ?? Base, "")).Event);

    public void Claim(RunEvent.Reserved reservation, RunId? run = null) => Assert.IsType<RunDecision.Granted>(
        Store.Claim(W, run ?? Run, Op(), new(reservation.Attempt.Id, 1), reservation.Inputs, Prompt));

    public LogCheckpoint WriteLog(RunEvent.Reserved reservation, TerminalAttemptOutcome outcome = TerminalAttemptOutcome.Succeeded,
        string report = "Checked.", ConversationMode? conversation = null, TaskId? subject = null, bool terminalHandoff = false, RunId? run = null)
    {
        var folder = Store.AttemptFolder(W, run ?? Run, reservation.Attempt.Task, reservation.Attempt.Id);
        var task = Read(run).Revisions[reservation.Attempt.Revision].Snapshot.Tasks[reservation.Attempt.Task];
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(
            At, reservation.Attempt.Id, task.Id, task.Title, task.Execution!, "Inspect", "codex", [])
        {
            RunBinding = new(W, run ?? Run, reservation.Attempt.Revision, reservation.Inputs.Id),
            Conversation = conversation ?? task.Conversation,
            ReadOnly = task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review,
            Subject = subject,
            Fix = Read(run).ReviewOf(reservation.Attempt.Id),
        }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("session-1")));
            if (outcome == TerminalAttemptOutcome.Cancelled)
            {
                log.Append(new AttemptEvent.CancelRequested(At));
            }

            if (outcome == TerminalAttemptOutcome.Interrupted)
            {
                log.Append(new AttemptEvent.InterruptRequested(At, "Stopped."));
            }

            log.Append(new AttemptEvent.Agent(At, outcome == TerminalAttemptOutcome.Failed ? new AgentEvent.Failed("Failed.") :
                new AgentEvent.Succeeded(report)));
            log.Append(new AttemptEvent.Exited(At, outcome == TerminalAttemptOutcome.Failed ? 1 : 0, ""));
            if (terminalHandoff)
            {
                log.Append(new AttemptEvent.HandedToTerminal(At, Project, "codex resume session-1"));
            }
        }
        return Checkpoint(folder);
    }

    public ResultRecord Complete(RunEvent.Reserved reservation, string report = "Checked.", ResultId? supersedes = null, RunId? run = null)
    {
        Claim(reservation, run);
        var checkpoint = WriteLog(reservation, report: report, run: run);
        Assert.IsType<RunDecision.Recorded>(Store.CloseAttempt(W, run ?? Run, Op(), reservation.Attempt.Id,
            TerminalAttemptOutcome.Succeeded, checkpoint));
        return Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(Store.AcceptReport(W, run ?? Run, Op(),
            reservation.Attempt.Id, reservation.Inputs.Id, report, supersedes)).Event).Result;
    }

    public static LogCheckpoint Checkpoint(string folder)
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder, "events.jsonl"));
        return new(bytes.LongLength, new(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    public static Blueprint Blueprint(string description = "", WorkSpec? work = null) => new(new("example.agent", 1), "Agent",
        work ?? new WorkSpec.Agent(AgentAccess.ReadOnly, false, PromptTemplate.Parse("{{brief}}")),
        [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous))
    {
        Description = description
    };

    public static TaskDefinition Task(TaskId? id = null, string model = "m1", string description = "", WorkSpec? work = null) =>
        new(id ?? T, Blueprint(description, work))
        {
            Title = "Plan",
            Execution = new(ClientId.Codex)
            {
                Model = model,
                Reasoning = "high"
            }
        };

    public static Workflow FixtureWorkflow(params TaskDefinition[] tasks)
    {
        var workflow = Workflow.Empty(W);
        foreach (var task in tasks.Length == 0 ? [Task()] : tasks)
        {
            workflow = Edit(workflow, TestNodes.Place(task, new(0, 0)));
        }

        return workflow;
    }

    public static Workflow Edit(Workflow workflow, WorkflowEdit edit) => Assert.IsType<EditResult.Applied>(workflow.Apply(edit)).Workflow;

    public static Workflow Connect(Workflow workflow, TaskId from, TaskId to, ConnectionKind kind = ConnectionKind.Dependency) =>
        Edit(workflow, new WorkflowEdit.Connect(new(from, to), kind));

    public static RunProblem Problem(RunDecision decision) => Assert.IsType<RunDecision.Rejected>(decision).Reason.Problem;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }
}

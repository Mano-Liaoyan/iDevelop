using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Xunit.Abstractions;

namespace IDevelop.Core.Tests;

/// <summary>
/// Runs the installed clients, so it spends subscription quota and CI never runs it. Set IDEVELOP_REAL_CLIENTS to the
/// clients' wire names, such as "claude-code,codex,pi,antigravity". The models default to small ones, and
/// IDEVELOP_REAL_MODEL_&lt;WIRE NAME&gt; changes one, such as IDEVELOP_REAL_MODEL_PI.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RealClientTests(ITestOutputHelper output) : IDisposable
{
    private static readonly Dictionary<ClientId, string> SmallModels = new()
    {
        [ClientId.ClaudeCode] = "claude-haiku-4-5",
        [ClientId.Codex] = "gpt-5.6-luna",
        [ClientId.Pi] = "openai-codex/gpt-5.6-luna",
        [ClientId.Antigravity] = "gemini-3.8-flash",
    };

    private readonly TempFolder _temp = new();

    public static TheoryData<string> Chosen => new(
        (Environment.GetEnvironmentVariable("IDEVELOP_REAL_CLIENTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } names
            ? names
            : ["none"]);

    public void Dispose() => _temp.Dispose();

    [RealClientsTheory]
    [MemberData(nameof(Chosen))]
    public async Task A_May_ask_node_asks_waits_and_finishes_with_the_answer(string wireName)
    {
        var client = Clients.ParseWireName(wireName) ?? throw new ArgumentException($"Unknown client {wireName}.");
        var project = _temp.Create("project");
        Process.Start(new ProcessStartInfo("git", ["init", "-q", project]) { UseShellExecute = false })!.WaitForExit();
        var clients = new ClientDirectory(CommandResolver.FromEnvironment());
        await clients.RefreshAsync();
        var model = Environment.GetEnvironmentVariable($"IDEVELOP_REAL_MODEL_{wireName.Replace('-', '_').ToUpperInvariant()}") ?? SmallModels[client];
        var option = ExecutionChoices.OfferedModel(clients.Current[client], model)
            ?? throw new InvalidOperationException($"{Clients.Name(client)} does not offer {model}: {clients.Current[client]}");
        var settings = new ExecutionSettings(client) { Model = model, Reasoning = option.ReasoningLevels.Contains("low") ? "low" : option.DefaultReasoning };
        var task = TestNodes.Implement(
            TestTasks.Design, "Write the fruit",
            "Write the name of one fruit, lowercase, into answer.txt in the current folder. You do not know which fruit I want, so ask me before you write anything.",
            execution: settings, conversation: ConversationMode.MayAsk);
        await using var runs = ProjectRuns.Open(project, clients);

        var asked = await Settles(runs, () => runs.Start(task));
        output.WriteLine($"{Clients.Name(client)} turn 1: {asked.Status} {asked.Pending} {asked.Detail}\n{asked.Result}");

        Assert.Equal(AttemptStatus.WaitingForInput, asked.Status);
        Assert.IsType<Pending.Question>(asked.Pending);
        Assert.False(File.Exists(Path.Combine(project, "answer.txt")));

        var answered = await Settles(runs, () => runs.Send(task, "banana", stopTurn: false));
        output.WriteLine($"{Clients.Name(client)} turn 2: {answered.Status} {answered.Detail}\n{answered.Result}");

        Assert.Equal((asked.Id, AttemptStatus.Succeeded, 2), (answered.Id, answered.Status, answered.Turns.Count));
        Assert.Equal("banana", File.ReadAllText(Path.Combine(project, "answer.txt")).Trim());
    }

    [RealClientsTheory]
    [MemberData(nameof(Chosen))]
    public async Task An_Architect_fills_the_two_empty_tasks_after_it_and_changes_no_file(string wireName)
    {
        var client = Clients.ParseWireName(wireName) ?? throw new ArgumentException($"Unknown client {wireName}.");
        var project = _temp.Create("project");
        File.WriteAllText(Path.Combine(project, "README.md"), "# sum\n\nA command-line tool that prints the sum of two integers.\n");
        Git(project, "init", "-q");
        Git(project, "add", ".");
        Git(project, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
        var clients = new ClientDirectory(CommandResolver.FromEnvironment());
        await clients.RefreshAsync();
        var model = Environment.GetEnvironmentVariable($"IDEVELOP_REAL_MODEL_{wireName.Replace('-', '_').ToUpperInvariant()}") ?? SmallModels[client];
        var option = ExecutionChoices.OfferedModel(clients.Current[client], model)
            ?? throw new InvalidOperationException($"{Clients.Name(client)} does not offer {model}: {clients.Current[client]}");
        var settings = new ExecutionSettings(client) { Model = model, Reasoning = option.ReasoningLevels.Contains("low") ? "low" : option.DefaultReasoning };
        var workflow = Workflow.Empty(WorkflowId.New())
            .Apply(new WorkflowEdit.Batch(
            [
                new WorkflowEdit.PlaceNode(TestTasks.Design, BuiltInBlueprints.Architect, new CanvasPoint(0, 0))
                {
                    Title = "Design sum",
                    Fields = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(
                        "brief", "Design the tool the README describes, in at most five sentences. Fill both empty tasks, one for parsing the arguments and one for printing the sum, and add one task that writes a test."),
                    Settings = new NodeSettings(settings, ConversationMode.Autonomous),
                },
                new WorkflowEdit.PlaceNode(TestTasks.Build, BuiltInBlueprints.Implement, new CanvasPoint(320, 0)),
                new WorkflowEdit.PlaceNode(TestTasks.Review, BuiltInBlueprints.Implement, new CanvasPoint(320, 190)),
                new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Design, TestTasks.Build), ConnectionKind.Dependency),
                new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Design, TestTasks.Review), ConnectionKind.Dependency),
            ])) is EditResult.Applied { Workflow: var drawn } ? drawn : throw new InvalidOperationException("The workflow did not build.");
        await using var runs = ProjectRuns.Open(project, clients);

        var record = await Settles(runs, () => runs.Start(workflow.Tasks[TestTasks.Design], PlanningContext.For(workflow, TestTasks.Design, BuiltInBlueprints.All, _ => false)));
        output.WriteLine($"{Clients.Name(client)}: {record.Status} {record.Detail}\n{record.Result}");

        Assert.Equal(AttemptStatus.Succeeded, record.Status);
        var proposal = Assert.IsType<ProposalRead.Ready>(Proposal.Read(record, BuiltInBlueprints.Find)).Proposal;
        var accepted = Assert.IsType<EditResult.Applied>(workflow.Apply(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false))).Workflow;
        foreach (var task in accepted.Tasks.Values)
        {
            output.WriteLine($"{task.Blueprint.Name} \"{task.Title}\": {string.Join(" | ", task.Fields.Select(field => $"{field.Key}={field.Value}"))}");
        }

        Assert.Equal([TestTasks.Build, TestTasks.Review], proposal.Fills.Select(fill => fill.Slot).Order().ToArray());
        Assert.NotEmpty(proposal.Nodes);
        Assert.All([TestTasks.Build, TestTasks.Review], slot => Assert.False(string.IsNullOrWhiteSpace(accepted.Tasks[slot].Field("instructions"))));
        Assert.Equal("", Git(project, "status", "--porcelain", "--untracked-files=all", "--", ".", ":!.idp", ":!.gitignore"));
    }

    private static string Git(string folder, params string[] arguments)
    {
        var git = Process.Start(new ProcessStartInfo("git", ["-C", folder, .. arguments]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        var text = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return text.Trim();
    }

    private static async Task<AttemptRecord> Settles(ProjectRuns runs, Func<object> act)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (runs.Latest.GetValueOrDefault(TestTasks.Design) is { Status: not AttemptStatus.Running } record && runs.Active.IsEmpty)
            {
                settled.TrySetResult(record);
            }
        }

        runs.Changed += OnChanged;
        try
        {
            var result = act();
            Assert.False(result is StartResult.Refused or SendResult.Refused, $"refused: {result}");
            return await settled.Task.WaitAsync(TimeSpan.FromMinutes(5));
        }
        finally
        {
            runs.Changed -= OnChanged;
        }
    }
}

internal sealed class RealClientsTheoryAttribute : TheoryAttribute
{
    public RealClientsTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IDEVELOP_REAL_CLIENTS")))
        {
            Skip = "Set IDEVELOP_REAL_CLIENTS to run the installed clients.";
        }
    }
}

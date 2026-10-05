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

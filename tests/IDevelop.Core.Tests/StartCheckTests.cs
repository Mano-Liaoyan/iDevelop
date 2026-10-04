using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Execution.StartProblem;

namespace IDevelop.Core.Tests;

public class StartCheckTests
{
    private const string Folder = @"C:\project";

    private static readonly TaskId Id = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly ResolvedCommand CodexCommand = new("/usr/local/bin/codex", IsBatchShim: false);
    private static readonly ExecutionSettings SolHigh = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" };

    private static readonly Dictionary<ClientId, ClientStatus> CodexReady = new()
    {
        [ClientId.Codex] = new ClientStatus.Ready(CodexCommand, [new ModelOption("gpt-6-sol", "GPT-6-Sol", ["low", "medium", "high"]) { DefaultReasoning = "medium" }]),
    };

    [Fact]
    public void A_task_that_can_start_gets_its_command_arguments_and_prompt()
    {
        var plan = Assert.IsType<StartVerdict.Allowed>(StartCheck.Evaluate(SayHi(SolHigh), Folder, CodexReady)).Plan;

        Assert.Same(CodexCommand, plan.Command);
        Assert.Equal(
            ["exec", "--json", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=high", "--sandbox", "workspace-write", "--skip-git-repo-check", "-"],
            plan.Launch.Arguments.ToArray());
        Assert.Equal("# Say hi\n\nCreate hello.txt containing hi.\n\n## Acceptance criteria\n\nhello.txt holds hi.\n", plan.Launch.Stdin);
    }

    [Fact]
    public void Each_reason_a_task_cannot_start_is_reported_and_the_first_one_wins()
    {
        Dictionary<ClientId, ClientStatus> Only(ClientStatus status) => new() { [ClientId.Codex] = status };

        Assert.Equal(new NoAgent(), Problem(SayHi(null, instructions: ""), CodexReady));
        Assert.Equal(new ClientChecking(ClientId.Codex), Problem(SayHi(SolHigh), Only(new ClientStatus.Checking())));
        Assert.Equal(new ClientChecking(ClientId.Codex), Problem(SayHi(SolHigh), new Dictionary<ClientId, ClientStatus>()));
        Assert.Equal(
            new ClientMissing(ClientId.Codex, "No codex command was found on PATH."),
            Problem(SayHi(SolHigh, instructions: ""), Only(new ClientStatus.Missing("No codex command was found on PATH."))));
        Assert.Equal(
            new ClientUnready(ClientId.Codex, "Codex is not signed in. Run codex login in a terminal."),
            Problem(SayHi(SolHigh), Only(new ClientStatus.Unready("Codex is not signed in. Run codex login in a terminal."))));
        Assert.Equal(new NoModel(ClientId.Codex), Problem(SayHi(new ExecutionSettings(ClientId.Codex)), CodexReady));
        Assert.Equal(new ModelNotOffered(ClientId.Codex, "gpt-4"), Problem(SayHi(SolHigh with { Model = "gpt-4" }), CodexReady));
        Assert.Equal(new NoInstructions(), Problem(SayHi(SolHigh, instructions: " \n "), CodexReady));
    }

    [Fact]
    public void A_model_whose_Pi_provider_is_signed_out_cannot_start()
    {
        var pi = new Dictionary<ClientId, ClientStatus>
        {
            [ClientId.Pi] = new ClientStatus.Ready(
                new ResolvedCommand("/usr/local/bin/pi", IsBatchShim: false),
                [new ModelOption("openai-codex/gpt-5.5", "GPT-5.5 (openai-codex)", ["low"]) { Provider = "openai-codex", Problem = "Pi's sign-in for openai-codex is invalid." }]),
        };

        Assert.Equal(
            new ModelUnready(ClientId.Pi, "openai-codex/gpt-5.5", "Pi's sign-in for openai-codex is invalid."),
            Problem(SayHi(new ExecutionSettings(ClientId.Pi) { Model = "openai-codex/gpt-5.5", Reasoning = "low" }), pi));
    }

    [Theory]
    [InlineData("gpt-6-sol", null, "low,medium,high")]
    [InlineData("gpt-6-sol", "ultra", "low,medium,high")]
    [InlineData("no-levels", "high", "")]
    public void A_reasoning_level_the_model_does_not_offer_cannot_start(string model, string? reasoning, string offered)
    {
        var clients = new Dictionary<ClientId, ClientStatus>
        {
            [ClientId.Codex] = new ClientStatus.Ready(
                CodexCommand,
                [new ModelOption("gpt-6-sol", "GPT-6-Sol", ["low", "medium", "high"]), new ModelOption("no-levels", "No levels", [])]),
        };

        var problem = Assert.IsType<ReasoningNotOffered>(Problem(SayHi(new ExecutionSettings(ClientId.Codex) { Model = model, Reasoning = reasoning }), clients));

        Assert.Equal((ClientId.Codex, model, reasoning, offered), (problem.Client, problem.Model, problem.Reasoning, string.Join(",", problem.Offered)));
    }

    [Fact]
    public void A_batch_shim_refuses_an_argument_cmd_could_misread_before_anything_launches()
    {
        var clients = new Dictionary<ClientId, ClientStatus>
        {
            [ClientId.Antigravity] = new ClientStatus.Ready(new ResolvedCommand(@"C:\npm\agy.cmd", IsBatchShim: true), [new ModelOption("weird%model", "Weird", [])]),
        };

        Assert.Equal(
            new UnsafeArgument(ClientId.Antigravity, "weird%model"),
            Problem(SayHi(new ExecutionSettings(ClientId.Antigravity) { Model = "weird%model" }), clients));
    }

    // cmd.exe cannot work in a folder that starts with \\, and would run the client in the Windows folder instead.
    [Fact]
    public void A_batch_shim_cannot_start_in_a_network_folder_and_a_program_can()
    {
        var pi = new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" };
        Dictionary<ClientId, ClientStatus> PiAt(string path, bool isBatchShim) => new()
        {
            [ClientId.Pi] = new ClientStatus.Ready(new ResolvedCommand(path, isBatchShim), [new ModelOption("deepseek/deepseek-v4-pro", "DeepSeek V4 Pro", ["high"])]),
        };

        Assert.Equal(new UncProjectFolder(ClientId.Pi), Problem(SayHi(pi), PiAt(@"C:\npm\pi.cmd", isBatchShim: true), folder: @"\\wsl.localhost\Ubuntu\home\me\repo"));
        Assert.IsType<StartVerdict.Allowed>(StartCheck.Evaluate(SayHi(pi), @"Z:\repo", PiAt(@"C:\npm\pi.cmd", isBatchShim: true)));
        Assert.IsType<StartVerdict.Allowed>(StartCheck.Evaluate(SayHi(pi), @"\\server\share\repo", PiAt(@"C:\tools\pi.exe", isBatchShim: false)));
    }

    [Fact]
    public void The_prompt_leaves_out_a_blank_title_and_blank_acceptance_criteria()
    {
        var task = new TaskDefinition(Id) { Title = " ", Instructions = "\nCreate hello.txt.\n" };

        Assert.Equal("Create hello.txt.\n", Prompt.For(task));
    }

    private static TaskDefinition SayHi(ExecutionSettings? execution, string instructions = "Create hello.txt containing hi.") =>
        new(Id) { Title = "Say hi", Instructions = instructions, AcceptanceCriteria = "hello.txt holds hi.", Execution = execution };

    private static StartProblem Problem(TaskDefinition task, IReadOnlyDictionary<ClientId, ClientStatus> clients, string folder = Folder) =>
        Assert.IsType<StartVerdict.Blocked>(StartCheck.Evaluate(task, folder, clients)).Problem;
}

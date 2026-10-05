using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Execution.StartProblem;

namespace IDevelop.Core.Tests;

public class StartCheckTests
{
    private const string Folder = @"C:\project";

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
            ["exec", "--json", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=high", "-c", "approval_policy=never", "--sandbox", "workspace-write", "--skip-git-repo-check", "-"],
            plan.Launch.Arguments.ToArray());
        const string prompt = "# Say hi\n\nCreate hello.txt containing hi.\n\n## Acceptance criteria\n\nhello.txt holds hi.\n";
        Assert.Equal(prompt, plan.Launch.Stdin);
        Assert.Equal(prompt, plan.Request.Prompt);
    }

    [Fact]
    public void A_continuation_resumes_the_session_with_the_message_and_needs_no_instructions()
    {
        var plan = Assert.IsType<StartVerdict.Allowed>(
            StartCheck.Evaluate(SayHi(SolHigh, instructions: ""), Folder, CodexReady, new Resumption("thread-1", "banana"))).Plan;

        Assert.Equal(
            ["exec", "resume", "--json", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=high", "-c", "approval_policy=never", "--skip-git-repo-check", "-c", "sandbox_mode=workspace-write", "thread-1", "-"],
            plan.Launch.Arguments.ToArray());
        Assert.Equal("banana", plan.Launch.Stdin);
    }

    [Fact]
    public void Codex_arguments_pass_through_an_npm_codex_cmd_shim()
    {
        var shim = new Dictionary<ClientId, ClientStatus>
        {
            [ClientId.Codex] = new ClientStatus.Ready(new ResolvedCommand(@"C:\npm\codex.cmd", IsBatchShim: true), [new ModelOption("gpt-6-sol", "GPT-6-Sol", ["high"])]),
        };

        var plan = Assert.IsType<StartVerdict.Allowed>(StartCheck.Evaluate(SayHi(SolHigh), Folder, shim)).Plan;

        Assert.Contains("approval_policy=never", plan.Launch.Arguments);
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
        Assert.Equal(new FieldMissing("Instructions"), Problem(SayHi(SolHigh, instructions: " \n "), CodexReady));
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

    [Theory]
    [InlineData("Say hi", "Create hello.txt containing hi.", "hello.txt holds hi.")]
    [InlineData(" ", "\nCreate hello.txt.\n", "")]
    [InlineData("  Two words \n", "  Line one\n\n  Line three  \n", " \n ")]
    [InlineData("审查", "Write {{title}} and {{#x}}braces{{/x}} literally.\n## Not a heading", "  First\nSecond  ")]
    [InlineData("", "", "Only criteria")]
    public void The_built_in_Implement_renders_the_single_task_prompt_byte_for_byte(string title, string instructions, string criteria)
    {
        var task = TestNodes.Implement(TestTasks.Design, title, instructions, criteria);

        Assert.Equal(PhaseThreePrompt(title, instructions, criteria), AgentWork.Prompt(new NodeContext(task, "")));
    }

    [Fact]
    public void A_May_ask_node_reads_how_to_ask_after_its_template()
    {
        var task = TestNodes.Implement(TestTasks.Design, "Say hi", "Create hello.txt.", conversation: ConversationMode.MayAsk);

        Assert.Equal(
            "# Say hi\n\nCreate hello.txt.\n\n" +
            "If you cannot go on without an answer from the person, ask instead of guessing. End your final message with this block and nothing after it, where the question is your own:\n\n" +
            "```idevelop\n{\"status\": \"asking\", \"question\": \"...\"}\n```\n\n" +
            "When you have finished the work, end without that block.\n",
            AgentWork.Prompt(new NodeContext(task, "")));
    }

    [Fact]
    public void A_read_only_agent_launches_in_the_clients_read_only_mode()
    {
        var reader = new Blueprint(
            new BlueprintKey("team.reader", 1), "Reader",
            new WorkSpec.Agent(AgentAccess.ReadOnly, Proposes: false, PromptTemplate.Parse("Read {{title}}.")), [],
            new NodeSettings(SolHigh, ConversationMode.Autonomous));

        var plan = Assert.IsType<StartVerdict.Allowed>(StartCheck.Evaluate(new TaskDefinition(TestTasks.Design, reader) { Title = "it" }, Folder, CodexReady)).Plan;

        Assert.Equal("Read it.", plan.Request.Prompt);
        Assert.Equal("exec --json -m gpt-6-sol -c model_reasoning_effort=high -c approval_policy=never --sandbox read-only --skip-git-repo-check -", string.Join(" ", plan.Launch.Arguments));
    }

    /// <summary>The prompt single-task execution sent, as it was written before typed nodes.</summary>
    private static string PhaseThreePrompt(string title, string instructions, string criteria)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add($"# {title.Trim()}");
        }

        parts.Add(instructions.Trim());
        if (!string.IsNullOrWhiteSpace(criteria))
        {
            parts.Add("## Acceptance criteria");
            parts.Add(criteria.Trim());
        }

        return string.Join("\n\n", parts) + "\n";
    }

    private static TaskDefinition SayHi(ExecutionSettings? execution, string instructions = "Create hello.txt containing hi.") =>
        TestNodes.Implement(TestTasks.Design, "Say hi", instructions, "hello.txt holds hi.", execution);

    private static StartProblem Problem(TaskDefinition task, IReadOnlyDictionary<ClientId, ClientStatus> clients, string folder = Folder) =>
        Assert.IsType<StartVerdict.Blocked>(StartCheck.Evaluate(task, folder, clients)).Problem;
}

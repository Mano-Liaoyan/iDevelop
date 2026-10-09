using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

/// <summary>
/// A planner chooses each new task's client, model, and reasoning from what this machine has ready, with a reason. A
/// choice this machine cannot run falls back, and says why.
/// </summary>
public sealed class AgentChoiceTests
{
    private static readonly TaskId Architect = TestTasks.Design;
    private static readonly TaskId Backend = TestTasks.Build;
    private static readonly TaskId Frontend = TestTasks.Review;
    private static readonly Guid Plan = Guid.Parse("019a9d2e-6100-7000-8000-000000000091");
    private static readonly ExecutionSettings Sol = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" };
    private static readonly ExecutionSettings Haiku = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5" };
    private static readonly ImmutableArray<string> Levels = ["low", "medium", "high", "xhigh"];

    /// <summary>A library blueprint with a default agent of its own.</summary>
    private static readonly Blueprint Careful = new(
        new BlueprintKey("team.careful", 1), "Careful implement", BuiltInBlueprints.Implement.Work, BuiltInBlueprints.Implement.Fields,
        new NodeSettings(Haiku, ConversationMode.MayAsk));

    /// <summary>The types a proposal may add, from <c>type-1</c>: the built-ins, then <see cref="Careful"/> as <c>type-6</c>.</summary>
    private static readonly ImmutableArray<Blueprint> Types = [.. BuiltInBlueprints.All, Careful];

    /// <summary>
    /// Claude Code and Codex ready, Pi ready with one provider signed out, Antigravity CLI not ready, so it offers nothing.
    /// </summary>
    private static readonly ImmutableDictionary<ClientId, ClientStatus> Machine = new Dictionary<ClientId, ClientStatus>
    {
        [ClientId.ClaudeCode] = new ClientStatus.Ready(new ResolvedCommand("claude", IsBatchShim: false),
        [
            new ModelOption("claude-opus-5-5", "Claude Opus 5.5", Levels) { DefaultReasoning = "high" },
            new ModelOption("claude-haiku-4-5", "Claude Haiku 4.5", Levels) { DefaultReasoning = "high" },
        ]),
        [ClientId.Codex] = new ClientStatus.Ready(new ResolvedCommand("codex", IsBatchShim: false),
        [
            new ModelOption("gpt-6.1-sol", "GPT-6.1-Sol", ["low", "medium", "high", "xhigh", "max"]) { DefaultReasoning = "low" },
            new ModelOption("gpt-5.5", "GPT-5.5", ["low", "medium", "high"]) { DefaultReasoning = "medium" },
            new ModelOption("codex-mini", "codex-mini", []),
        ]),
        [ClientId.Pi] = new ClientStatus.Ready(new ResolvedCommand("pi", IsBatchShim: false),
        [
            new ModelOption("deepseek-flash", "DeepSeek V4.1 Flash", ["low", "high"]) { Provider = "deepseek" },
            new ModelOption("gpt-6-astra", "GPT-6 Astra", ["high"]) { Provider = "openai-codex", Problem = "Sign in to openai-codex in Pi again." },
        ]),
        [ClientId.Antigravity] = new ClientStatus.Unready("agy models did not answer within 30 seconds."),
    }.ToImmutableDictionary();

    [Fact]
    public void A_planners_contract_lists_each_ready_client_with_its_usable_models_and_their_levels()
    {
        var contract = Planning(Machine).Contract(ConversationMode.Autonomous);

        Assert.EndsWith("""

            Agents you may choose:
            - claude-code (Claude Code):
              - claude-opus-5-5, Claude Opus 5.5. Reasoning: low, medium, high, xhigh.
              - claude-haiku-4-5, Claude Haiku 4.5. Reasoning: low, medium, high, xhigh.
            - codex (Codex):
              - gpt-6.1-sol, GPT-6.1-Sol. Reasoning: low, medium, high, xhigh, max.
              - gpt-5.5, GPT-5.5. Reasoning: low, medium, high.
              - codex-mini. No reasoning levels.
            - pi (Pi), which has no read-only mode:
              - deepseek-flash, DeepSeek V4.1 Flash. Reasoning: low, high.
            """, contract);
        Assert.DoesNotContain("antigravity", contract);
        Assert.DoesNotContain("gpt-6-astra", contract);
        Assert.DoesNotContain("Do not choose agents", contract);
    }

    [Fact]
    public void The_contract_asks_for_an_agent_and_a_reason_for_each_new_task_and_says_how_to_fit_it()
    {
        var contract = Planning(Machine).Contract(ConversationMode.Autonomous);

        Assert.Contains("""
             "add": [{"id": "new-1", "type": "type-1", "title": "...", "fields": {"key": "..."},
                      "agent": {"client": "...", "model": "...", "reasoning": "...", "reason": "..."}}],
            """, contract);
        Assert.Contains(
            "- \"agent\" chooses who carries out a task you add: a client and one of its models from the agents below, one of that model's reasoning levels, and a one-line reason that the person reads before accepting. Leave out \"reasoning\" for a model without levels. A slot keeps its own agent.\n",
            contract);
        Assert.Contains(
            "- Fit each choice to its task. Design, architecture, planning, and review need careful judgment, so give them a strong reasoning model at a high level. Small or mechanical edits need a fast model at a low level, and ordinary implementation needs something between. Choose for each task on its own, so that tasks of different weight do not all get the same agent.\n",
            contract);
    }

    [Fact]
    public void The_guidance_names_no_model_so_only_the_list_of_agents_follows_the_machine()
    {
        var other = Machine.SetItem(ClientId.Codex, new ClientStatus.Missing("No codex command was found on PATH."))
            .SetItem(ClientId.Antigravity, new ClientStatus.Ready(new ResolvedCommand("agy", IsBatchShim: false), [new ModelOption("gemini-3.8-flash-low", "Gemini 3.8 Flash (Low)", [])]));

        static string Guidance(string contract) => contract[..contract.IndexOf("\n\nAgents you may choose:", StringComparison.Ordinal)];

        Assert.Equal(Guidance(Planning(Machine).Contract(ConversationMode.Chat)), Guidance(Planning(other).Contract(ConversationMode.Chat)));
        Assert.EndsWith("- antigravity (Antigravity CLI):\n  - gemini-3.8-flash-low, Gemini 3.8 Flash (Low). No reasoning levels.", Planning(other).Contract(ConversationMode.Chat));
    }

    [Fact]
    public void Each_type_says_whether_it_only_reads_takes_no_agent_or_has_an_agent_of_its_own()
    {
        var contract = Planning(Machine).Contract(ConversationMode.Autonomous);

        Assert.Contains("- type-2: Plan. Plans the work toward a goal and proposes the tasks that carry it out. It changes no file. Fields: goal (Goal, required), constraints (Constraints). Reads only, so it needs a client with a read-only mode.\n", contract);
        Assert.Contains("- type-4: Review. ", contract);
        Assert.Matches(@"- type-4: Review\. [^\n]* Reads only, so it needs a client with a read-only mode\.\n", contract);
        Assert.Matches(@"- type-5: Approval\. [^\n]* Takes no agent\.\n", contract);
        Assert.Contains(" Its own agent is claude-code, claude-haiku-4-5. Leave out \"agent\" to keep it.\n", contract);
        Assert.Matches(@"- type-1: Implement\. [^\n]*\(Acceptance criteria\)\.\n", contract);
    }

    [Fact]
    public void Without_agents_the_contract_is_the_one_run_planners_have_always_read()
    {
        Assert.Equal("""
            When your plan is ready, end your final message with one proposal block. Nothing comes after the block. The person accepts the tasks they want from it:

            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "title": "...", "fields": {"key": "..."}}],
             "add": [{"id": "new-1", "type": "type-1", "title": "...", "fields": {"key": "..."}}],
             "connect": [{"from": "planner", "to": "new-1"}]}
            ```

            - "fill" gives work to an empty task that the person drew after you, by its slot.
            - "add" creates a task of one of the types below. Give each one an id of your own, such as new-1.
            - "connect" makes "to" wait for "from" and receive its result. Add "kind": "context" when "to" only reads the latest result of "from" and does not wait. "from" and "to" each name a slot, an id from "add", or "planner", which is you.
            - Write each field under its key, and leave out a field you have nothing for. Do not choose agents or models. The person does.

            Empty tasks you may fill:
            - None.

            Types you may add:
            - type-1: Approval. Waits for you to approve what the tasks before it handed on, or to send it back. Fields: checklist (What to check).
            """, new PlanningContext([], [BuiltInBlueprints.Approval]).Contract(ConversationMode.Autonomous));
        Assert.Equal(
            PlanningContext.None.Contract(ConversationMode.Chat),
            (PlanningContext.None with { Agents = PlanningContext.Offers(ImmutableDictionary<ClientId, ClientStatus>.Empty) }).Contract(ConversationMode.Chat));
    }

    [Fact]
    public void A_new_task_carries_the_agent_its_planner_chose_and_why()
    {
        var proposal = Ready("""
            {"status": "proposal",
             "add": [{"id": "rename", "type": "type-1", "title": "Rename the field",
                      "agent": {"client": "codex", "model": "gpt-5.5", "reasoning": "low", "reason": "A mechanical rename."}},
                     {"id": "design", "type": "type-3", "title": "Design the export"}]}
            """);

        Assert.Equal(new ProposedAgent("codex", "gpt-5.5", "low", "A mechanical rename."), proposal.Nodes[0].Agent);
        Assert.Null(proposal.Nodes[1].Agent);
    }

    [Theory]
    [InlineData("\"codex\"", "The agent of a is not an object.")]
    [InlineData("{\"client\": 3, \"model\": \"gpt-5.5\"}", "The \"client\" of a's agent is not text.")]
    [InlineData("{\"client\": \"codex\", \"reason\": [\"fast\"]}", "The \"reason\" of a's agent is not text.")]
    public void An_agent_that_cannot_be_read_stays_with_its_task_and_says_why(string agent, string problem)
    {
        var proposal = Ready($$"""{"status": "proposal", "add": [{"id": "a", "type": "type-1", "agent": {{agent}}}]}""");

        var read = Assert.Single(proposal.Nodes).Agent!;
        Assert.Equal(problem, read.Unreadable);
        Assert.Equal(new AgentCheck.Unusable($"The planner's agent could not be read. {problem}", "Agent unreadable"), AgentCheck.Of(read, BuiltInBlueprints.Implement, Machine));
    }

    [Fact]
    public void A_proposal_without_agents_reads_and_records_as_before()
    {
        var proposal = Ready("""{"status": "proposal", "add": [{"id": "a", "type": "type-1", "title": "A"}]}""");

        var chose = Ready("""{"status": "proposal", "add": [{"id": "a", "type": "type-1", "title": "A", "agent": {"client": "codex"}}]}""");

        Assert.Null(Assert.Single(proposal.Nodes).Agent);
        Assert.DoesNotContain("\"agent\":", RunJournal.Canonical(proposal));
        Assert.Contains("\"agent\":{\"client\":\"codex\",", RunJournal.Canonical(chose));
    }

    [Theory]
    [InlineData("codex", "gpt-5.5", "low", "codex", "gpt-5.5", "low")]
    [InlineData("Codex", "GPT-5.5", "Low", "codex", "gpt-5.5", "low")]
    [InlineData("Claude Code", "claude-opus-5-5", "xhigh", "claude-code", "claude-opus-5-5", "xhigh")]
    [InlineData("codex", "codex-mini", null, "codex", "codex-mini", null)]
    [InlineData("pi", "deepseek-flash", "high", "pi", "deepseek-flash", "high")]
    public void A_choice_this_machine_offers_is_usable_under_the_catalogs_own_names(
        string client, string model, string? reasoning, string wire, string id, string? level)
    {
        var settings = new ExecutionSettings(Clients.ParseWireName(wire)!.Value) { Model = id, Reasoning = level };

        Assert.Equal(new AgentCheck.Usable(settings), AgentCheck.Of(new ProposedAgent(client, model, reasoning, "Why."), BuiltInBlueprints.Implement, Machine));
    }

    [Theory]
    [InlineData(null, "gpt-5.5", "low", "The planner chose no client.", "No client chosen")]
    [InlineData("cursor", "gpt-5.5", "low", "The planner chose cursor, which is not a client iDevelop runs.", "Unknown client")]
    [InlineData("antigravity", "gemini-3.8-flash-low", null, "The planner chose Antigravity CLI, which isn't ready.", "Antigravity CLI isn't ready")]
    [InlineData("codex", null, "low", "The planner chose no Codex model.", "No model chosen")]
    [InlineData("codex", "gpt-9", "low", "The planner chose gpt-9, which Codex doesn't offer.", "Model not offered")]
    [InlineData("pi", "gpt-6-astra", "high", "The planner chose GPT-6 Astra, which isn't ready. Sign in to openai-codex in Pi again.", "Model not ready")]
    [InlineData("codex", "gpt-5.5", null, "The planner chose no reasoning level for GPT-5.5.", "No reasoning level")]
    [InlineData("codex", "gpt-5.5", "max", "The planner chose max reasoning, which GPT-5.5 doesn't offer.", "Level not offered")]
    [InlineData("codex", "codex-mini", "high", "The planner chose high reasoning, but codex-mini takes no reasoning level.", "Level not offered")]
    public void A_choice_this_machine_cannot_run_is_unusable_and_says_why(string? client, string? model, string? reasoning, string problem, string brief)
    {
        Assert.Equal(
            new AgentCheck.Unusable(problem, brief),
            AgentCheck.Of(new ProposedAgent(client, model, reasoning, "Why."), BuiltInBlueprints.Implement, Machine));
    }

    [Fact]
    public void A_client_that_is_missing_or_still_checked_is_unusable_for_now()
    {
        var agent = new ProposedAgent("codex", "gpt-5.5", "low", null);

        Assert.Equal(
            new AgentCheck.Unusable("The planner chose Codex, which isn't installed.", "Codex isn't installed"),
            AgentCheck.Of(agent, BuiltInBlueprints.Implement, Machine.SetItem(ClientId.Codex, new ClientStatus.Missing("No codex command was found on PATH."))));
        Assert.Equal(
            new AgentCheck.Unusable("The planner chose Codex, which iDevelop is still checking.", "Codex is being checked"),
            AgentCheck.Of(agent, BuiltInBlueprints.Implement, Machine.SetItem(ClientId.Codex, new ClientStatus.Checking())));
        Assert.Equal(
            new AgentCheck.Unusable("The planner chose Codex, which isn't installed.", "Codex isn't installed"),
            AgentCheck.Of(agent, BuiltInBlueprints.Implement, Machine.Remove(ClientId.Codex)));
    }

    [Fact]
    public void A_type_that_only_reads_needs_a_client_with_a_read_only_mode()
    {
        var pi = new ProposedAgent("pi", "deepseek-flash", "high", null);

        Assert.Equal(
            new AgentCheck.Unusable("The planner chose Pi, which has no read-only mode, and a Plan only reads.", "Pi can't run read-only"),
            AgentCheck.Of(pi, BuiltInBlueprints.Plan, Machine));
        Assert.IsType<AgentCheck.Unusable>(AgentCheck.Of(pi, BuiltInBlueprints.Review, Machine));
        Assert.IsType<AgentCheck.Usable>(AgentCheck.Of(new ProposedAgent("claude-code", "claude-opus-5-5", "high", null), BuiltInBlueprints.Review, Machine));
    }

    [Fact]
    public void No_choice_and_a_type_that_takes_no_agent_check_as_none()
    {
        Assert.IsType<AgentCheck.None>(AgentCheck.Of(null, BuiltInBlueprints.Implement, Machine));
        Assert.IsType<AgentCheck.None>(AgentCheck.Of(new ProposedAgent("codex", "gpt-5.5", "low", null), BuiltInBlueprints.Approval, Machine));
    }

    [Fact]
    public void Accepting_gives_each_new_task_its_chosen_agent_before_the_fallback_and_its_types_own()
    {
        var workflow = ArchitectWithTwoSlots();
        var proposal = Ready("""
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "fields": {"instructions": "Build the endpoint."}}],
             "add": [{"id": "build", "type": "type-1"}, {"id": "check", "type": "type-4"}, {"id": "sign", "type": "type-5"}, {"id": "careful", "type": "type-6"}]}
            """);
        var chosen = new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "xhigh" };
        var agents = new Dictionary<TaskId, ExecutionSettings>
        {
            [Id(proposal, "build")] = chosen,
            [Id(proposal, "sign")] = chosen,
            [Id(proposal, "careful")] = chosen,
            [Backend] = chosen,
        };

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false, Sol, agents));

        Assert.Equal(
            [
                ("build", chosen, ConversationMode.Autonomous),
                ("check", Sol, ConversationMode.Autonomous),
                ("sign", null, ConversationMode.Autonomous),
                ("careful", chosen, ConversationMode.MayAsk),
                ("slot-1", null, ConversationMode.Autonomous),
            ],
            proposal.Nodes.Select(node => (node.Name, node.Id)).Append((Name: "slot-1", Id: Backend))
                .Select(task => (task.Name, accepted.Tasks[task.Id].Execution, accepted.Tasks[task.Id].Conversation)));
    }

    /// <summary>A planner's context on <paramref name="clients"/>, with the built-ins and <see cref="Careful"/> as its types.</summary>
    private static PlanningContext Planning(IReadOnlyDictionary<ClientId, ClientStatus> clients) =>
        new PlanningContext([], Types) { Agents = PlanningContext.Offers(clients) };

    private static TaskId Id(Proposal proposal, string name) => proposal.Nodes.Single(node => node.Name == name).Id;

    /// <summary>An Architect at the origin with a dependency on each of two empty Implement nodes the person drew.</summary>
    private static Workflow ArchitectWithTwoSlots() =>
        Workflow.Empty(new WorkflowId(Guid.Parse("019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a91")))
            .Must(new PlaceNode(Architect, BuiltInBlueprints.Architect, new CanvasPoint(0, 0))
            {
                Title = "Design export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("brief", "Export the report as CSV."),
                Settings = new NodeSettings(Sol, ConversationMode.Autonomous),
            })
            .Must(TestNodes.Place(TestNodes.Implement(Backend, "Backend"), new CanvasPoint(320, 0)))
            .Must(TestNodes.Place(TestNodes.Implement(Frontend, "Frontend"), new CanvasPoint(320, 190)))
            .Must(new Connect(new ConnectionKey(Architect, Backend), ConnectionKind.Dependency))
            .Must(new Connect(new ConnectionKey(Architect, Frontend), ConnectionKind.Dependency));

    /// <summary>The proposal of an Architect attempt whose one turn ended with <paramref name="json"/> in its block.</summary>
    private static Proposal Ready(string json)
    {
        var record = AttemptReducer.Replay(
        [
            new AttemptEvent.Requested(AttemptEvents.T0, new AttemptId(Guid.Parse("019a9d2e-6000-7000-8000-000000000091")), Architect, "Design export", Sol, "p", "codex", [])
            {
                Planning = new PlanningHandles(Plan, [Backend, Frontend], [.. Types.Select(blueprint => blueprint.Key)]),
            },
            new AttemptEvent.Agent(AttemptEvents.T0, new AgentEvent.SessionStarted("01a108d7-464d-77a3-8908-a36f38ce6c91")),
            new AttemptEvent.Agent(AttemptEvents.T0, new AgentEvent.Succeeded($"Here is the plan.\n\n```idevelop\n{json}\n```")),
            new AttemptEvent.Exited(AttemptEvents.T0, 0, ""),
        ])!;
        return Assert.IsType<ProposalRead.Ready>(Proposal.Read(record, key => Types.FirstOrDefault(blueprint => blueprint.Key == key))).Proposal;
    }
}

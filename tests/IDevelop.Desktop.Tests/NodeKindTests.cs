using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>A node's kind from its blueprint: built-in ids, derivation chains, and what the work does.</summary>
public sealed class NodeKindTests : IDisposable
{
    private static readonly WorkSpec.Agent EditAgent = new(AgentAccess.Edit, Proposes: false, PromptTemplate.Parse("Do {{goal}}."));

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static Blueprint Agent(string id, AgentAccess access, bool proposes, BlueprintKey? derivedFrom = null) =>
        new(new BlueprintKey(id, 1), id, EditAgent with { Access = access, Proposes = proposes },
            [new FieldSpec("goal", "Goal", FieldShape.Text, Required: false, "")], new NodeSettings(null, ConversationMode.Autonomous))
        {
            DerivedFrom = derivedFrom,
        };

    /// <summary>A blueprint with the work and fields of <paramref name="like"/>, derived from <paramref name="derivedFrom"/>.</summary>
    private static Blueprint Like(string id, Blueprint like, BlueprintKey? derivedFrom) =>
        new(new BlueprintKey(id, 1), id, like.Work, like.Fields, like.Defaults) { DerivedFrom = derivedFrom };

    private static Blueprint Derived(string id, Blueprint from) => Like(id, from, from.Key);

    private static NodeKind KindOf(Blueprint blueprint, params Blueprint[] known) =>
        NodeKinds.Of(blueprint, key => known.FirstOrDefault(candidate => candidate.Key == key));

    [Fact]
    public void Each_built_in_has_its_own_kind()
    {
        Assert.Equal(
            [NodeKind.Implement, NodeKind.Plan, NodeKind.Architect, NodeKind.Review, NodeKind.Approval],
            BuiltInBlueprints.All.Select(blueprint => KindOf(blueprint)));
    }

    [Fact]
    public void A_blueprint_derived_from_a_built_in_has_the_built_ins_kind_even_where_its_work_reads_otherwise()
    {
        // An Architect's work is a proposing read-only agent, which by work alone would be a Plan.
        Assert.Equal(
            [NodeKind.Implement, NodeKind.Plan, NodeKind.Architect, NodeKind.Review, NodeKind.Approval],
            BuiltInBlueprints.All.Select(blueprint => KindOf(Derived($"acme.{blueprint.Name.ToLowerInvariant()}", blueprint))));
    }

    [Fact]
    public void A_chain_through_a_library_blueprint_reaches_the_built_in()
    {
        var spec = Derived("acme.spec", BuiltInBlueprints.Architect);
        var strictSpec = Derived("acme.strict-spec", spec);

        Assert.Equal(NodeKind.Architect, KindOf(strictSpec, spec));
    }

    [Fact]
    public void A_chain_that_breaks_or_loops_falls_back_to_what_the_work_does()
    {
        var orphan = Agent("acme.orphan", AgentAccess.Edit, proposes: false, new BlueprintKey("acme.gone", 1));
        var first = Agent("acme.first", AgentAccess.ReadOnly, proposes: true, new BlueprintKey("acme.second", 1));
        var second = Agent("acme.second", AgentAccess.Edit, proposes: false, first.Key);

        Assert.Equal(NodeKind.Implement, KindOf(orphan));
        Assert.Equal((NodeKind.Plan, NodeKind.Implement), (KindOf(first, first, second), KindOf(second, first, second)));
    }

    [Fact]
    public void Without_a_built_in_in_its_chain_a_blueprint_takes_the_kind_of_its_work()
    {
        Blueprint[] blueprints =
        [
            Like("acme.review", BuiltInBlueprints.Review, derivedFrom: null),
            Like("acme.sign-off", BuiltInBlueprints.Approval, derivedFrom: null),
            Agent("acme.roadmap", AgentAccess.ReadOnly, proposes: true),
            Agent("acme.fix", AgentAccess.Edit, proposes: false),
            Agent("acme.audit", AgentAccess.ReadOnly, proposes: false),
        ];

        Assert.Equal(
            [NodeKind.Review, NodeKind.Approval, NodeKind.Plan, NodeKind.Implement, NodeKind.ReadOnlyAgent],
            blueprints.Select(blueprint => KindOf(blueprint)));
    }

    [Fact]
    public void Each_kind_has_a_label_an_icon_and_a_style_class()
    {
        Assert.Equal(
            [
                ("Implement", "IconKindImplement", "kind-implement"),
                ("Plan", "IconKindPlan", "kind-plan"),
                ("Architect", "IconKindArchitect", "kind-architect"),
                ("Review", "IconKindReview", "kind-review"),
                ("Approval", "IconKindApproval", "kind-approval"),
                ("Read-only agent", "IconKindReadOnlyAgent", "kind-readonlyagent"),
            ],
            Enum.GetValues<NodeKind>().Select(NodeKinds.Info).Select(info => (info.Label, info.Icon, info.StyleClass)));
    }

    [AvaloniaFact]
    public void A_node_follows_its_chain_through_the_copy_its_workflow_embeds_when_no_library_has_it()
    {
        var spec = Derived("acme.spec", BuiltInBlueprints.Architect);
        var strictSpec = Derived("acme.strict-spec", spec);
        var folder = _temp.Seed(
            new WorkflowEdit.PlaceNode(TestTasks.Design, spec, new CanvasPoint(105, 90)) { Title = "Spec" },
            new WorkflowEdit.PlaceNode(TestTasks.Build, strictSpec, new CanvasPoint(465, 90)) { Title = "Strict spec" },
            new WorkflowEdit.PlaceNode(TestTasks.Review, BuiltInBlueprints.Review, new CanvasPoint(825, 90)) { Title = "Check" });

        var shell = Shell.Open(folder);

        Assert.Equal(
            [(NodeKind.Architect, true), (NodeKind.Architect, true), (NodeKind.Review, false)],
            new[] { "Spec", "Strict spec", "Check" }.Select(title => (TaskNodeViewModel)shell.Node(title).DataContext!).Select(node => (node.Kind, node.IsLibrary)));
    }

    [AvaloniaFact]
    public void A_library_entry_carries_its_kind_and_says_it_is_from_a_library()
    {
        var folder = _temp.Create("library");
        BlueprintLibrary.Project(folder).Save(Derived("acme.spec", BuiltInBlueprints.Architect));

        var blueprints = Shell.Open(folder).Window.ViewModel.Canvas!.Blueprints;

        Assert.Equal(
            [(NodeKind.Implement, false), (NodeKind.Plan, false), (NodeKind.Architect, false), (NodeKind.Review, false), (NodeKind.Approval, false), (NodeKind.Architect, true)],
            blueprints.Groups.SelectMany(group => group.Entries).Select(entry => (entry.Kind, entry.IsLibrary)));
    }
}

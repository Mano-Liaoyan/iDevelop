using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The three-node fan-out the owner checked the canvas with: "Design the API", then "Build the API", which keeps running
/// until its gate opens, and "Write docs", a Chat node that waits for the person after its turn. Generate's planner
/// proposes three more nodes.
/// </summary>
internal static class FanOut
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Docs = TestTasks.Review;
    private const string Goal = "Add CSV export to the reports page.";

    private const string Proposal = """
        I split the export into an endpoint, a button, and its tests.

        ```idevelop
        {"status": "proposal",
         "add": [{"id": "api", "type": "type-1", "title": "Export API", "fields": {"instructions": "Add the CSV endpoint."}},
                 {"id": "button", "type": "type-1", "title": "Export button", "fields": {"instructions": "Add the button."}},
                 {"id": "tests", "type": "type-1", "title": "Export tests", "fields": {"instructions": "Test the export."}}],
         "connect": [{"from": "planner", "to": "api"}, {"from": "api", "to": "button"}, {"from": "api", "to": "tests"}]}
        ```
        """;

    public static NodeFixture Fixture()
    {
        var f = new NodeFixture(
            AppTempFolder.TaskAt(Design, "Design the API", 105, 190, Agy, "Design the export endpoint."),
            AppTempFolder.TaskAt(Build, "Build the API", 465, 90, Agy, "Build the export endpoint."),
            AppTempFolder.TaskAt(Docs, "Write docs", 465, 290, Agy, "Document the export.", ConversationMode.Chat),
            Dependency(Design, Build), Dependency(Design, Docs));
        f.Install(On().Choose(
            (Fresh("Design the API"), Reply("s-design", "The design is ready.")),
            (Fresh("Build the API"), Reply("s-build", "Built.", On().WaitForFile(f.Gate("build")))),
            (Fresh("Write docs"), Reply("s-docs", "Here is a first draft of the export docs. Should they cover large reports too?")),
            (Fresh(Goal), Reply("s-plan", Proposal))));
        return f;
    }

    /// <summary>Runs the workflow and waits until "Build the API" runs and "Write docs" waits for the person.</summary>
    public static void Run(Shell shell)
    {
        shell.Click(shell.Find<Avalonia.Controls.Button>("RunWorkflow"));
        shell.WaitForState("Write docs", NodeState.InputRequired);
        shell.WaitForState("Build the API", NodeState.Running);
    }

    /// <summary>Generates from <see cref="Goal"/> and waits until the planner's proposal shows.</summary>
    public static void Generate(Shell shell)
    {
        shell.Click(shell.Find<Avalonia.Controls.Button>("GenerateWorkflow"));
        shell.Enter(Goal);
        shell.Click(shell.Find<Avalonia.Controls.Button>("GenerateSubmit"));
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.GenerateProgress == GenerateProgress.Reviewing, "the proposal shows");
    }
}

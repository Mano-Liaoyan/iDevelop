using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The three-task fan-out the owner checked the canvas with: "Design the API", then "Build the API", which keeps running
/// until its gate opens, and "Write docs", a Chat task that waits for the person after its turn. Generate's planner
/// proposes three more tasks.
/// </summary>
internal static class FanOut
{
    public static readonly TaskId Design = TestTasks.Design;
    public static readonly TaskId Build = TestTasks.Build;
    public static readonly TaskId Docs = TestTasks.Review;
    public const string Goal = "Add CSV export to the reports page.";

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

    public static WorkflowRunFixture Fixture()
    {
        var f = new WorkflowRunFixture(
            TaskAt(Design, "Design the API", 105, 190, WorkflowRunFixture.Codex, "Design the export endpoint."),
            TaskAt(Build, "Build the API", 465, 90, WorkflowRunFixture.Codex, "Build the export endpoint."),
            TaskAt(Docs, "Write docs", 465, 290, WorkflowRunFixture.Codex, "Document the export.", ConversationMode.Chat),
            Dependency(Design, Build), Dependency(Design, Docs));
        f.Answer("Design the API", f.Says("The design is ready."))
            .Answer("Build the API", f.Says("Built.", gate: "build"))
            .Answer("Write docs", f.Says("Here is a first draft of the export docs. Should they cover large reports too?"))
            .Answer(Goal, f.Says(Proposal));
        return f;
    }

    /// <summary>Starts the workflow's run and waits until "Build the API" runs and "Write docs" waits for the person.</summary>
    public static void Run(Shell shell)
    {
        shell.StartRun();
        shell.WaitForCard("Write docs", "Waiting for you");
        shell.WaitForCard("Build the API", "Running");
    }

    /// <summary>Generates from <see cref="Goal"/> and waits until the planner's proposal shows.</summary>
    public static void Generate(Shell shell)
    {
        shell.Click(shell.Find<Avalonia.Controls.Button>("GenerateWorkflow"));
        shell.Type(Goal);
        shell.Click(shell.Find<Avalonia.Controls.Button>("GenerateSubmit"));
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.GenerateProgress == GenerateProgress.Reviewing, "the proposal shows");
    }
}

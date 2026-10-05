using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Theme;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>Typed nodes and the format 3 file, through the real main window.</summary>
public sealed class TypedNodeTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void A_format_2_workflow_opens_converted_says_so_and_saves_as_format_3()
    {
        var folder = _temp.Create("legacy");
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, ".idp", "workflows")).FullName, "019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json");
        File.Copy(Fixture.Path("storage-change-format2.json"), file);

        var shell = Shell.Open(folder);

        Assert.Equal(
            "iDevelop converted this workflow from format 2. Its 3 tasks became Implement nodes, and its review connection became a dependency. Save to keep it in format 3.",
            shell.Status);
        Assert.True(shell.ShowsUnsavedChanges);
        var kind = shell.InCard<KindTile>("Review the storage change", "CardKind");
        Assert.Equal(("Implement", "Implement version 1"), (AutomationProperties.GetName(kind), AutomationProperties.GetHelpText(kind)));

        shell.Press(Key.S, RawInputModifiers.Control);

        Assert.False(shell.ShowsUnsavedChanges);
        Assert.StartsWith("{\n  \"format\": \"idevelop.workflow/3\",", File.ReadAllText(file));
        Assert.Null(WorkflowDocument.Open(folder).Converted);
    }

    [AvaloniaFact]
    public void The_inspector_shows_the_tasks_type_its_fields_and_its_conversation_mode()
    {
        var folder = _temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, instructions: "Draft it."));
        var shell = Shell.Open(folder);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Implement", shell.Find<TextBlock>("TaskType").Text);
        Assert.Equal("Draft it.", shell.Find<TextBox>("TaskInstructions").Text);
        Assert.Equal("", shell.Find<TextBox>("TaskAcceptanceCriteria").Text);
        Assert.Equal(["Autonomous", "May ask", "Chat"], shell.Pick("TaskConversation", "Chat"));

        Assert.Equal(
            ("Waits for you after every turn.", "The task waits for you after every turn, until you mark it done."),
            (shell.Find<TextBlock>("ConversationNote").Text, ToolTip.GetTip(shell.Find<TextBlock>("ConversationNote"))));
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Equal(ConversationMode.Chat, WorkflowDocument.Open(folder).Current.Tasks[TestTasks.Design].Conversation);
    }
}

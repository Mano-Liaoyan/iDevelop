using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using IDevelop.Desktop.Inspector;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The inspector's shared columns: every view ends its actions at the panel's 12 px inset and its values 64 px from the
/// panel's edge, at the narrowest and the widest inspector, and a narrow inspector puts each picker under its label.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class InspectorLayoutTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public InspectorLayoutTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(520)]
    public void The_workflow_the_task_and_the_connection_views_share_two_right_edges(double width)
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(
            _temp.Seed(
                TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90),
                new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)),
            _fakes.DiscoverAsync().Result);
        shell.SizeInspector(width);
        Assert.Equal(width, shell.Find<Control>("Inspector").Bounds.Width);

        Assert.Equal("actions at 12, values at 64", Edges(shell));

        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Write it.");
        Assert.True(shell.Find<Button>("RevertInstructions").IsEffectivelyVisible);
        Assert.True(shell.Find<Control>("PermissionNote").IsEffectivelyVisible);

        Assert.Equal("actions at 12, values at 64", Edges(shell));

        shell.Click(shell.ConnectionInto("Build"));

        Assert.Equal("actions at 12, values at 64", Edges(shell));
    }

    [AvaloniaTheory]
    [InlineData(280, true)]
    [InlineData(320, false)]
    [InlineData(520, false)]
    public void A_picker_sits_under_its_label_only_in_an_inspector_narrower_than_320(double width, bool under)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));

        AssertUnder(shell, under, "TaskClient", "TaskModel", "TaskReasoning", "TaskConversation");

        shell.Window.ViewModel.Canvas!.Blueprints.Derive(BuiltInBlueprints.Implement);
        shell.Render();

        AssertUnder(shell, under, "BlueprintName", "BlueprintClient", "BlueprintConversation");
    }

    /// <summary>Each editor sits 4 px under its label and starts where the label starts, or sits beside it.</summary>
    private static void AssertUnder(Shell shell, bool under, params string[] ids)
    {
        foreach (var id in ids)
        {
            var editor = shell.Find<Control>(id);
            var label = editor.FindAncestorOfType<InspectorRow>()!.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Label");
            var (top, bottom) = (shell.Bounds(editor).Top, shell.Bounds(label).Bottom);
            Assert.True(under ? Math.Abs(top - bottom - 4) < 0.5 : top < bottom, $"{id} starts at {top}, and its label ends at {bottom}.");
            Assert.Equal(under, shell.Bounds(editor).Left == shell.Bounds(label).Left);
        }
    }

    [AvaloniaFact]
    public void Every_labelled_row_of_a_task_leads_with_a_glyph_or_a_tile()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.Click(shell.Header(shell.Node("Design")));

        var rows = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Layout != RowLayout.Full).ToArray();

        Assert.Equal(
            ["Instructions", "Acceptance criteria", "Client", "Model", "Reasoning", "Conversation", "Type", "Version", "Description"],
            rows.Select(row => row.Label));
        Assert.All(rows, row => Assert.True(row.Icon is not null || row.Lead is not null, $"{row.Label} has no glyph."));
        Assert.Equal(
            ["IconEdit", "IconCheckmark", "IconAgent", "IconModel", "IconReasoning", "IconConversation", null, "IconVersion", "IconDescription"],
            rows.Select(row => IconKey(row.Icon)));
    }

    [AvaloniaFact]
    public void Kinds_and_blueprints_read_as_names_in_strong_text()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        var labels = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Classes.Contains("entry"))
            .Select(row => (row.Label, ((ISolidColorBrush)row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Label").Foreground!).Color))
            .ToArray();

        var strong = ((ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Light, "TextStrongBrush")!).Color;
        Assert.Equal(
            [("Implement", strong), ("Implement", strong), ("Plan", strong), ("Architect", strong), ("Review", strong), ("Approval", strong)],
            labels);
    }

    /// <summary>
    /// The distinct right insets, in px from the inspector's edge, of the actions and of the values it shows now. A row's
    /// actions are its trailing glyphs, such as a revert arrow and an info glyph, or Place and More, which end together.
    /// </summary>
    private static string Edges(Shell shell)
    {
        var inspector = shell.Find<Control>("Inspector");
        var right = shell.Bounds(inspector).Right;
        var shown = inspector.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0).ToArray();
        bool InRow(Control control) => control.FindAncestorOfType<InspectorRow>() is not null;
        string? Id(Control control) => AutomationProperties.GetAutomationId(control);

        var actions = shown.Where(control => Id(control) is "InspectorMore" or "InspectorTools" || control.Name == "PART_Trail");
        var values = shown.Where(control =>
            control is ComboBox
            || control is TextBox && (Id(control) == "InspectorFilter" || InRow(control))
            || control is Button { Classes: var classes } && classes.Contains("rowLink")
            || control is TextBlock && control.FindAncestorOfType<InspectorRow>() is { } row && row.Classes.Contains("entry") && control.Name != "PART_Label");

        string Insets(IEnumerable<Control> controls) =>
            string.Join(" and ", controls.Select(control => Math.Round(right - shell.Bounds(control).Right, 1)).Distinct().Order());
        return $"actions at {Insets(actions)}, values at {Insets(values)}";
    }

    private static string? IconKey(Geometry? icon) =>
        icon is null ? null
        : new[] { "IconEdit", "IconCheckmark", "IconAgent", "IconModel", "IconReasoning", "IconConversation", "IconVersion", "IconDescription", "IconField" }
            .SingleOrDefault(key => Application.Current!.TryGetResource(key, null, out var resource) && ReferenceEquals(resource, icon));
}

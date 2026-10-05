using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Blueprints;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The palette, Derive, Save as blueprint, and the blueprint editor, through the real main window.</summary>
public sealed class BlueprintTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    // The App's personal library outlives a test, so a test that saves there leaves it empty for the next one.
    public void Dispose()
    {
        if (Application.Current is App { PersonalBlueprints: { } personal } && Directory.Exists(personal))
        {
            Directory.Delete(personal, recursive: true);
        }

        _temp.Dispose();
    }

    [AvaloniaFact]
    public void A_built_in_offers_place_and_derive_and_no_edit()
    {
        var shell = Shell.Open(_temp.Create("plan"));

        Assert.Equal(["Implement"], EntryNames(shell, "BUILT-IN"));
        Assert.True(Button(shell, "Implement", "PlaceBlueprint").IsEffectivelyVisible);
        Assert.True(Button(shell, "Implement", "DeriveBlueprint").IsEffectivelyVisible);
        Assert.False(Button(shell, "Implement", "EditBlueprint").IsEffectivelyVisible);
        Assert.Empty(EntryNames(shell, "PROJECT"));
    }

    [AvaloniaFact]
    public void A_derived_type_edited_to_version_2_leaves_the_first_node_on_version_1_after_a_save_and_a_reopen()
    {
        var project = _temp.Create("plan");
        var shell = Shell.Open(project);

        Press(shell, "Implement", "DeriveBlueprint");
        Assert.Equal("Derived from Implement, version 1. Saving makes version 1.", shell.Find<TextBlock>("BlueprintCaption").Text);
        shell.Find<TextBox>("BlueprintName").Text = "Bug fix";
        shell.Click(shell.InView<Button>("SaveBlueprint"));

        Assert.Equal("Saved Bug fix, version 1, to the project library.", shell.Status);
        Assert.Equal(["Bug fix"], EntryNames(shell, "PROJECT"));
        var key = Assert.Single(BlueprintLibrary.Project(project).Read().Blueprints).Key;
        Assert.Equal(1, key.Version);

        Press(shell, "Bug fix", "PlaceBlueprint");
        Assert.Equal(["Bug fix", "Version 1"], [shell.Find<TextBlock>("TaskType").Text ?? "", shell.Find<TextBlock>("TaskTypeVersion").Text ?? ""]);
        shell.Find<TextBox>("TaskTitle").Text = "First";
        ClearSelection(shell);

        Press(shell, "Bug fix", "EditBlueprint");
        Assert.Equal(
            "Version 1 in the project library. Saving makes version 2, and nodes placed before keep their version.",
            shell.Find<TextBlock>("BlueprintCaption").Text);
        shell.Find<TextBox>("BlueprintTemplate").Text = "Fix it: {{instructions}}";
        shell.Click(shell.InView<Button>("SaveBlueprint"));

        Assert.Equal("Saved Bug fix, version 2, to the project library.", shell.Status);
        Press(shell, "Bug fix", "PlaceBlueprint");
        Assert.Equal("Version 2", shell.Find<TextBlock>("TaskTypeVersion").Text);
        shell.Find<TextBox>("TaskTitle").Text = "Second";
        shell.Press(Key.S, RawInputModifiers.Control);

        var reopened = Shell.Open(project);
        reopened.Click(reopened.Header(reopened.Node("First")));
        Assert.Equal(["Bug fix", "Version 1"], [reopened.Find<TextBlock>("TaskType").Text ?? "", reopened.Find<TextBlock>("TaskTypeVersion").Text ?? ""]);
        reopened.Click(reopened.Header(reopened.Node("Second")));
        Assert.Equal("Version 2", reopened.Find<TextBlock>("TaskTypeVersion").Text);
        var workflow = WorkflowDocument.Open(project).Current;
        Assert.Equal([key, key with { Version = 2 }], workflow.Blueprints.Keys);
    }

    [AvaloniaFact]
    public void Save_as_blueprint_makes_the_tasks_values_and_agent_the_defaults_of_a_personal_blueprint()
    {
        var execution = new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-astra" };
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, execution, "Draft it.", ConversationMode.Chat)));
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.InView<Button>("SaveAsBlueprint"));
        Assert.Equal("Design", shell.Find<TextBox>("BlueprintName").Text);
        Assert.Equal(["Draft it.", ""], [.. Rows(shell).Select(row => ById<TextBox>(row, "FieldDefault").Text ?? "")]);
        Assert.Equal("Codex", shell.Picked("BlueprintClient"));
        Assert.Equal("gpt-6-astra", shell.Find<TextBox>("BlueprintModel").Text);
        Assert.Equal("Chat", shell.Picked("BlueprintConversation"));
        shell.Click(shell.InView<RadioButton>("BlueprintInPersonal"));
        shell.Click(shell.InView<Button>("SaveBlueprint"));

        Assert.Equal("Saved Design, version 1, to the personal library.", shell.Status);
        var personal = ((App)Application.Current!).PersonalBlueprints!;
        var saved = Assert.Single(BlueprintLibrary.Personal(personal).Read().Blueprints);
        Assert.Equal(BuiltInBlueprints.Implement.Key, saved.DerivedFrom);
        Assert.Empty(BlueprintLibrary.Project(shell.Window.ViewModel.Canvas!.Document.ProjectFolder).Read().Blueprints);

        ClearSelection(shell);
        Press(shell, "Design", "PlaceBlueprint");
        Assert.Equal("Draft it.", shell.Find<TextBox>("TaskInstructions").Text);
        Assert.Equal("Chat", shell.Picked("TaskConversation"));
    }

    [AvaloniaFact]
    public void The_editor_names_what_keeps_a_blueprint_from_saving_and_a_new_field_reaches_the_inspector()
    {
        var project = _temp.Create("plan");
        var shell = Shell.Open(project);
        Press(shell, "Implement", "DeriveBlueprint");

        shell.Find<TextBox>("BlueprintTemplate").Text = "{{#instructions}}Fix it.";
        Assert.Equal("The template: {{#instructions}} has no {{/instructions}}.", shell.Find<TextBlock>("BlueprintProblem").Text);
        Assert.False(shell.Find<Button>("SaveBlueprint").IsEffectivelyEnabled);

        shell.Find<TextBox>("BlueprintTemplate").Text = "Fix {{bug}}.\n{{instructions}}";
        Assert.EndsWith("'s template reads {{bug}}, which is not a field.", shell.Find<TextBlock>("BlueprintProblem").Text);

        shell.Click(shell.InView<Button>("AddBlueprintField"));
        var added = Rows(shell)[^1];
        ById<TextBox>(added, "FieldKey").Text = "bug";
        ById<TextBox>(added, "FieldLabel").Text = "Bug";
        shell.Render();
        Assert.False(shell.Find<TextBlock>("BlueprintProblem").IsEffectivelyVisible);
        shell.Click(shell.InView<Button>("SaveBlueprint"));

        Press(shell, "Implement copy", "PlaceBlueprint");
        Assert.Equal("Bug", shell.Find<TextBox>("TaskBug").GetValue(AutomationProperties.NameProperty));
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Equal(["acceptanceCriteria", "bug", "instructions"], WorkflowDocument.Open(project).Current.Blueprints.Values.Single().Fields.Select(field => field.Key).Order());
    }

    [AvaloniaFact]
    public void Cancel_closes_the_editor_and_saves_nothing()
    {
        var project = _temp.Create("plan");
        var shell = Shell.Open(project);
        Press(shell, "Implement", "DeriveBlueprint");

        shell.Click(shell.InView<Button>("CancelBlueprint"));

        Assert.False(shell.Has<StackPanel>("BlueprintEditor"));
        Assert.True(shell.Find<TextBlock>("InspectorHint").IsEffectivelyVisible);
        Assert.False(Directory.Exists(Path.Combine(project, ".idp")));
    }

    private static Control[] Containers(Visual root) =>
        [.. root.GetSelfAndVisualDescendants().OfType<ItemsControl>().SelectMany(items => items.GetRealizedContainers())];

    private static Control[] Entries(Shell shell, string heading) =>
        [.. Containers(shell.Find<StackPanel>("Palette"))
            .Where(container => container.DataContext is BlueprintEntryViewModel entry && Group(shell, entry) == heading)];

    private static string Group(Shell shell, BlueprintEntryViewModel entry) =>
        shell.Window.ViewModel.Canvas!.Blueprints.Groups.Single(group => group.Entries.Contains(entry)).Heading;

    private static string[] EntryNames(Shell shell, string heading) =>
        [.. Entries(shell, heading).Select(entry => ((BlueprintEntryViewModel)entry.DataContext!).Name)];

    private static Button Button(Shell shell, string name, string automationId) =>
        ById<Button>(
            Containers(shell.Find<StackPanel>("Palette")).Single(container => container.DataContext is BlueprintEntryViewModel entry && entry.Name == name),
            automationId);

    private static void Press(Shell shell, string name, string automationId)
    {
        var button = Button(shell, name, automationId);
        button.BringIntoView();
        shell.Render();
        shell.Click(button);
    }

    private static Control[] Rows(Shell shell) => Containers(shell.Find<ItemsControl>("BlueprintFields"));

    private static T ById<T>(Visual root, string automationId) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static void ClearSelection(Shell shell) =>
        shell.Click(shell.Editor.TranslatePoint(default, shell.Window)!.Value + new Vector(600, 600));
}

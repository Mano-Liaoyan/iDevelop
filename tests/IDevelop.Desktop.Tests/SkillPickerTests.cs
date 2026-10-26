using Avalonia.Automation;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Clients;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A node's skill picker (#93) lists the skills its client loads on this machine, from fake skill folders. A node starts
/// with no choice, which shows every skill ticked; the first untick keeps exactly the ticked skills, and the revert arrow
/// goes back to all of them. A chosen skill that is not installed stays listed, and the inspector says the task runs
/// without it. Antigravity CLI's picker is off and says why.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class SkillPickerTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly ExecutionSettings Claude = new(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _personal;

    public SkillPickerTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _personal = _temp.Create("personal-skills");
        Skill(_personal, "find-skills", "Finds skills to install.");
    }

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void Every_skill_shows_ticked_until_the_first_untick_keeps_the_others_and_revert_brings_all_back()
    {
        var folder = Project(TaskAt(Design, "Design", 105, 90, Claude));
        var shell = Open(folder);
        Assert.Equal("All skills", Summary(shell));
        Assert.False(shell.Find<ToggleButton>("TaskSkills").IsChecked);
        Assert.False(shell.Find<Button>("RevertSkills").IsEffectivelyVisible);

        OpenList(shell);

        // simplify is one of Claude Code's bundled skills.
        Assert.Equal([("find-skills", true), ("simplify", true), ("verify-idevelop", true)], Ticks(shell));
        Assert.Equal("Finds skills to install.", ToolTip.GetTip(Choice(shell, "find-skills")));
        // The owner decided on 2026-10-11 that Claude Code's subagents keep every skill, and the picker says so.
        Assert.Equal("Claude Code's subagents can still use every skill.", shell.Find<TextBlock>("SkillListFooter").Text);

        shell.Click(Choice(shell, "find-skills"));

        Assert.Equal(["simplify", "verify-idevelop"], Execution(shell).Skills!);
        Assert.Equal([("find-skills", false), ("simplify", true), ("verify-idevelop", true)], Ticks(shell));
        Assert.Equal("2 skills", Summary(shell));
        Assert.True(shell.Find<Button>("RevertSkills").IsEffectivelyVisible);

        shell.Click(Choice(shell, "simplify"));

        Assert.Equal("1 skill", Summary(shell));

        shell.Click(Choice(shell, "verify-idevelop"));

        Assert.Empty(Execution(shell).Skills!);
        Assert.Equal("No skills", Summary(shell));
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Empty(WorkflowDocument.OpenProject(folder).Single().Current.Tasks[Design].Execution!.Skills!);

        shell.Click(shell.Find<Button>("RevertSkills"));

        Assert.Null(Execution(shell).Skills);
        Assert.Equal("All skills", Summary(shell));
        Assert.Equal([("find-skills", true), ("simplify", true), ("verify-idevelop", true)], Ticks(shell));
        Assert.Equal(Claude, Execution(shell));
    }

    [AvaloniaFact]
    public void A_claude_code_that_cannot_list_its_built_in_skills_says_a_choice_leaves_them_out()
    {
        var folder = Project(TaskAt(Design, "Design", 105, 90, Claude));
        _fakes.Install("claude", ClaudeSignedIn, FakeRule.On("-p").Lacking("--include-partial-messages")
            .WaitForLine("initialize").ListSkills("claude", [".claude/skills"], builtIn: ["compact", "simplify"])
            .WaitForLine("reload_skills")
            .EchoId("""{"type":"control_response","response":{"subtype":"error","request_id":$id,"error":"Unsupported control request subtype: reload_skills"}}"""));
        var shell = Shell.Open(folder, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));

        OpenList(shell);

        Assert.Equal([("verify-idevelop", true)], Ticks(shell));
        Assert.Equal("Claude Code doesn't list its built-in skills, so a choice leaves them out. Claude Code's subagents can still use every skill.",
            shell.Find<TextBlock>("SkillListFooter").Text);
        Assert.True(shell.Find<TextBlock>("SkillListFooter").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Pis_picker_says_the_projects_skills_appear_once_the_folder_is_trusted_in_pi()
    {
        var personal = _temp.Create("pi-skills");
        Skill(personal, "find-skills", "Finds skills to install.");
        _fakes.Install("pi", FakeRule.On("--mode", "rpc", "--no-session").Answer(
                ("get_available_models", FakeRule.On().Replay(Fixture.Path("pi-rpc-models.jsonl"))),
                ("get_commands", FakeRule.On().ListSkills("pi", [personal]))).WaitForStdinEnd(),
            PiProvider("deepseek", "ready"), PiProvider("openai-codex", "invalid"));
        var folder = _temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" }));
        Skill(Path.Combine(folder, ".pi", "skills"), "deploy", "Deploys the site.");
        var shell = Shell.Open(folder, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));

        OpenList(shell);

        Assert.Equal([("find-skills", true)], Ticks(shell));
        Assert.Equal("This project's skills appear after the folder is trusted in Pi. Check agents again once it is.",
            shell.Find<TextBlock>("SkillListFooter").Text);
        Assert.True(shell.Find<TextBlock>("SkillListFooter").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Codex_and_pi_nodes_list_their_own_clients_skills()
    {
        var personal = _temp.Create("agents-skills");
        Skill(personal, "imagegen", "Makes images.");
        _fakes.Install("codex", CodexModels, CodexSignedIn, CodexSkills(personal));
        _fakes.Install("pi", PiRpc(personal), PiProvider("deepseek", "ready"), PiProvider("openai-codex", "invalid"));
        var folder = _temp.Seed(
            TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" }),
            TaskAt(TestTasks.Build, "Build", 405, 90, new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" }));
        Skill(Path.Combine(folder, ".agents", "skills"), "release", "Cuts a release.");
        Skill(Path.Combine(folder, ".pi", "skills"), "deploy", "Deploys the site.");
        var shell = Shell.Open(folder, _fakes.DiscoverAsync().Result);

        shell.Click(shell.Header(shell.Node("Design")));
        OpenList(shell);
        Assert.Equal([("imagegen", true), ("release", true)], Ticks(shell));
        Assert.False(shell.Find<TextBlock>("SkillListFooter").IsEffectivelyVisible);

        shell.Click(shell.Header(shell.Node("Build")));
        OpenList(shell);
        Assert.Equal([("deploy", true), ("imagegen", true), ("release", true)], Ticks(shell));
    }

    [AvaloniaFact]
    public void An_open_picker_keeps_its_own_colors_and_draws_a_comboboxs_chevron()
    {
        var shell = Open(Project(TaskAt(Design, "Design", 105, 90, Claude)));
        var picker = shell.Find<ToggleButton>("TaskSkills");

        OpenList(shell);
        shell.Render();

        // Fluent's checked toggle would fill the box with the accent and turn its text white.
        Assert.True(picker.IsChecked);
        var presenter = picker.GetVisualDescendants().OfType<ContentPresenter>().Single(part => part.Name == "PART_ContentPresenter");
        Assert.True(presenter.Background is null or ISolidColorBrush { Color.A: 0 }, $"The open box is filled with {presenter.Background}.");
        Assert.Equal(Color(picker.Foreground), Color(presenter.Foreground));
        var chevron = picker.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(part => part.Name == "Chevron");
        var glyph = shell.Find<ComboBox>("TaskModel").GetVisualDescendants().OfType<PathIcon>().Single(part => part.Name == "DropDownGlyph");
        Assert.Equal((glyph.Data!.Bounds, glyph.Bounds.Width, Color(glyph.Foreground)), (chevron.Data!.Bounds, chevron.Bounds.Width, Color(chevron.Fill)));
        var (drawn, box) = (shell.Bounds(glyph), shell.Bounds(chevron));
        Assert.Equal((drawn.Right, drawn.Center.Y - shell.Bounds(shell.Find<ComboBox>("TaskModel")).Top),
            (box.Right, box.Center.Y - shell.Bounds(picker).Top));

        static Color? Color(IBrush? brush) => (brush as ISolidColorBrush)?.Color;
    }

    [AvaloniaFact]
    public void A_chosen_skill_says_which_plugin_skills_claude_code_lets_in_with_it()
    {
        Skill(_personal, "probeplug-probe-alpha", "A plugin's alpha.", name: "probeplug:probe-alpha");
        var shell = Open(Project(TaskAt(Design, "Design", 105, 90, Claude with { Skills = ["verify-idevelop"] })));
        Skill(Path.Combine(shell.Window.ViewModel.Canvas!.Document.ProjectFolder, ".claude", "skills"), "probe-alpha", "Alpha.");
        shell.Click(shell.Find<Button>("RefreshAgents"));

        OpenList(shell);
        shell.WaitUntil(() => Ticks(shell).Any(tick => tick.Item1 == "probe-alpha"), "probe-alpha listed");
        Assert.DoesNotContain(Shell.ById<TextBlock>(shell.Window, "SkillLetsIn"), text => text.IsEffectivelyVisible);

        shell.Click(Choice(shell, "probe-alpha"));

        var note = Assert.Single(Shell.ById<TextBlock>(shell.Window, "SkillLetsIn"), text => text.IsEffectivelyVisible);
        Assert.Equal("Also lets in probeplug:probe-alpha.", Visible(note.Text));
        Assert.Same(Choice(shell, "probe-alpha"), note.FindAncestorOfType<CheckBox>());
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(520)]
    public void What_a_chosen_skill_lets_in_wraps_only_between_names(double width)
    {
        Skill(_personal, "code-review", "Reviews.");
        Skill(_personal, "code-review-plugin", "Reviews a pull request.", name: "code-review:code-review");
        var shell = Open(Project(TaskAt(Design, "Design", 105, 90, Claude with { Skills = ["code-review"] })));
        shell.SizeInspector(width);

        OpenList(shell);
        shell.Render();

        var note = Assert.Single(Shell.ById<TextBlock>(shell.Window, "SkillLetsIn"), text => text.IsEffectivelyVisible);
        Assert.Equal("Also lets in code-review:code-review.", Visible(note.Text));
        // A line that is not the first starts after a space, never inside a name such as after "code-".
        Assert.All(note.TextLayout.TextLines.Skip(1), line => Assert.True(note.Text![line.FirstTextSourceIndex - 1] == ' ',
            $"At {width} px a line starts at \"{Visible(note.Text![line.FirstTextSourceIndex..])}\"."));
    }

    [AvaloniaFact]
    public void A_chosen_skill_that_is_not_installed_stays_listed_and_the_inspector_says_the_task_runs_without_it()
    {
        var shell = Open(Project(TaskAt(Design, "Design", 105, 90, Claude with { Skills = ["gone", "verify-idevelop"] })));

        shell.WaitUntil(() => shell.Find<TextBlock>("SkillsNote").IsEffectivelyVisible, "the note about the missing skill");
        Assert.Equal("gone is not installed, so the task runs without it.", shell.Find<TextBlock>("SkillsNote").Text);
        Assert.Equal("2 skills", Summary(shell));
        Assert.Equal("gone, verify-idevelop", ToolTip.GetTip(shell.Find<ToggleButton>("TaskSkills")));

        OpenList(shell);

        Assert.Equal([("find-skills", false), ("simplify", false), ("verify-idevelop", true), ("gone (not installed)", true)], Ticks(shell));

        shell.Click(Choice(shell, "gone (not installed)"));

        Assert.Equal(["verify-idevelop"], Execution(shell).Skills!);
        Assert.False(shell.Find<TextBlock>("SkillsNote").IsEffectivelyVisible);
        Assert.Equal([("find-skills", false), ("simplify", false), ("verify-idevelop", true)], Ticks(shell));
    }

    [AvaloniaFact]
    public void Antigravitys_picker_is_off_and_says_it_uses_all_of_its_skills()
    {
        Install(_fakes, ClientId.Antigravity);
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" })),
            _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.False(shell.Find<ToggleButton>("TaskSkills").IsEffectivelyEnabled);
        Assert.Equal("All skills", Summary(shell));
        Assert.Equal("Antigravity CLI uses all of its installed skills; it can't limit them per task.", shell.Find<TextBlock>("SkillsNote").Text);
        Assert.False(shell.Find<Control>("SkillList").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Choosing_another_client_clears_the_choice_and_no_agent_hides_the_picker()
    {
        Install(_fakes, ClientId.Codex);
        var shell = Open(Project(TaskAt(Design, "Design", 105, 90, Claude with { Skills = ["verify-idevelop"] })));
        Assert.Equal("1 skill", Summary(shell));

        shell.Pick("TaskClient", "Codex");

        Assert.Null(Execution(shell).Skills);
        Assert.Equal("All skills", Summary(shell));

        shell.Pick("TaskClient", "None");

        Assert.False(shell.Find<ToggleButton>("TaskSkills").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_listing_that_fails_says_why_the_task_still_runs_with_its_choice_and_checking_the_agents_again_lists_once_more()
    {
        _fakes.Install("claude", ClaudeSignedIn, FakeRule.On("-p").Lacking("--include-partial-messages").WaitForLine("initialize")
            .EchoId("""{"type":"control_response","response":{"subtype":"error","request_id":$id,"error":"settings are invalid"}}"""));
        var shell = Shell.Open(Project(TaskAt(Design, "Design", 105, 90, Claude with { Skills = ["verify-idevelop"] }, instructions: "Design it.")),
            _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.Find<ToggleButton>("TaskSkills"));
        shell.WaitUntil(() => shell.Find<TextBlock>("SkillListNote").Text?.StartsWith("iDevelop could not", StringComparison.Ordinal) == true, "the listing's failure");

        Assert.Equal("iDevelop could not list the skills Claude Code loads. Claude Code did not list its skills: settings are invalid. Check agents again to list them once more.",
            shell.Find<TextBlock>("SkillListNote").Text);
        Assert.Empty(Ticks(shell));
        // The task is not refused: it runs with its choice as it is, and the row says the list couldn't be checked.
        Assert.Equal("Claude Code's skill list couldn't be read, so it gets the chosen skills as they are, unchecked.", shell.Find<TextBlock>("SkillsNote").Text);
        Assert.False(shell.ShowsAny("StartProblem"), shell.Text("StartProblem"));
        Assert.Equal("1 skill", Summary(shell));

        _fakes.Install("claude", ClaudeSignedIn, ClaudeSkills(_personal));
        shell.Click(shell.Find<Button>("RefreshAgents"));
        shell.WaitUntil(() => Ticks(shell).Length > 0, "the skills listed once more");

        Assert.Equal([("find-skills", false), ("simplify", false), ("verify-idevelop", true)], Ticks(shell));
        Assert.False(shell.Find<TextBlock>("SkillListNote").IsEffectivelyVisible);
        Assert.False(shell.Find<TextBlock>("SkillsNote").IsEffectivelyVisible);
    }

    /// <summary>A project whose Claude Code skills folder holds verify-idevelop, and whose fake Claude Code also lists find-skills.</summary>
    private string Project(params WorkflowEdit[] edits)
    {
        var folder = _temp.Seed(edits);
        Skill(Path.Combine(folder, ".claude", "skills"), "verify-idevelop", "Drives the built app's real window.");
        return folder;
    }

    private Shell Open(string folder)
    {
        _fakes.Install("claude", ClaudeSignedIn, ClaudeSkills(_personal));
        var shell = Shell.Open(folder, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));
        return shell;
    }

    private static void OpenList(Shell shell)
    {
        shell.Click(shell.Find<ToggleButton>("TaskSkills"));
        shell.WaitUntil(() => Shell.ById<CheckBox>(shell.Window, "SkillChoice").Any(), "the listed skills");
    }

    /// <summary>The text without its word joiners, which only keep a name on one line.</summary>
    private static string? Visible(string? text) => text?.Replace("\u2060", "", StringComparison.Ordinal);

    private static string Summary(Shell shell) => Shell.TextOf(shell.Find<ToggleButton>("TaskSkills"));

    private static (string, bool)[] Ticks(Shell shell) =>
        [.. Shell.ById<CheckBox>(shell.Window, "SkillChoice").Where(box => box.IsEffectivelyVisible).Select(box => (AutomationProperties.GetName(box)!, box.IsChecked == true))];

    private static CheckBox Choice(Shell shell, string label) =>
        Shell.ById<CheckBox>(shell.Window, "SkillChoice").Single(box => AutomationProperties.GetName(box) == label);

    private static ExecutionSettings Execution(Shell shell) => shell.Window.ViewModel.Canvas!.Document.Current.Tasks[Design].Execution!;

    /// <summary>A skill folder whose SKILL.md names it <paramref name="name"/>, or its folder's name.</summary>
    internal static void Skill(string folder, string directory, string description, string? name = null)
    {
        var skill = Directory.CreateDirectory(Path.Combine(folder, directory)).FullName;
        name ??= directory;
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\nInstructions iDevelop never reads.\n");
    }
}

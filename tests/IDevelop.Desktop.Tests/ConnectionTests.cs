using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

public sealed class ConnectionTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private string DesignThenBuild() => _temp.Seed(
        TaskAt(Design, "Design", 105, 90),
        TaskAt(Build, "Build", 405, 90),
        new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency));

    [AvaloniaFact]
    public void Dragging_an_output_onto_an_input_adds_a_dependency()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 405, 90)));

        shell.Drag(shell.Center(shell.Output("Design")), shell.Center(shell.Input("Build")));

        Assert.Equal([("Design", "Build", ConnectionKind.Dependency)], shell.Drawn());
        Assert.Equal(
            (Shell.Rounded(shell.Thumb(shell.Output("Design"))), Shell.Rounded(shell.Thumb(shell.Input("Build")))),
            Shell.Rounded(shell.Ends(Assert.Single(shell.Connections()))));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal("", shell.Status);
    }

    [AvaloniaFact]
    public void A_drop_that_would_close_a_cycle_shows_why_and_adds_nothing()
    {
        var shell = Shell.Open(DesignThenBuild());
        string? preview = null;

        shell.Drag(
            shell.Center(shell.Output("Build")),
            shell.Center(shell.Input("Design")),
            beforeRelease: () => preview = shell.Window.GetVisualDescendants().OfType<PendingConnection>().Single().Content as string);

        Assert.Equal("That would create a cycle: Build → Design → Build.", preview);
        Assert.Equal("That would create a cycle: Build → Design → Build.", shell.Status);
        Assert.Equal([("Design", "Build", ConnectionKind.Dependency)], shell.Drawn());
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Clicking_a_connection_after_a_task_shows_its_kind_buttons_which_change_its_kind()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);

        shell.Click(shell.ConnectionInto("Build"));

        Assert.False(shell.Has<TextBox>("TaskTitle"));
        Assert.False(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
        Assert.True(shell.Find<Button>("KindContext").IsEffectivelyEnabled);

        shell.Click(shell.Find<Button>("KindContext"));

        Assert.Equal([("Design", "Build", ConnectionKind.Context)], shell.Drawn());
        Assert.False(shell.Find<Button>("KindContext").IsEffectivelyEnabled);
        Assert.True(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Clicking_a_task_after_a_connection_shows_the_task_and_deselects_the_connection()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Click(shell.ConnectionInto("Build"));
        Assert.True(shell.Has<Button>("KindDependency"));

        shell.Click(shell.Header(shell.Node("Build")));

        Assert.Equal("Build", shell.Find<TextBox>("TaskTitle").Text);
        Assert.False(shell.Has<Button>("KindDependency"));
        Assert.False(BaseConnection.GetIsSelected(Assert.Single(shell.Connections())));
    }

    [AvaloniaTheory]
    [InlineData("ThemeLight", "#564ADE")]
    [InlineData("ThemeDark", "#6D7CFF")]
    public void A_connection_draws_in_its_source_kinds_stroke_and_a_context_connection_is_dashed(string theme, string stroke)
    {
        // Review sits below Build, so the connection from Design to Review leaves the row before it reaches Build's input.
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            TaskAt(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency)));
        shell.Click(shell.Find<RadioButton>(theme));
        var implement = Color.Parse(stroke);
        (string From, string To, Color Stroke, Color Arrow, bool Dashed)[] Strokes() =>
        [
            .. shell.Connections()
                .Select(connection => (Model: (ConnectionViewModel)connection.DataContext!, Connection: connection))
                .OrderBy(drawn => drawn.Model.From.Title)
                .ThenBy(drawn => drawn.Model.To.Title)
                .Select(drawn => (
                    drawn.Model.From.Title,
                    drawn.Model.To.Title,
                    ((ISolidColorBrush)drawn.Connection.Stroke!).Color,
                    ((ISolidColorBrush)drawn.Connection.Fill!).Color,
                    drawn.Connection.StrokeDashArray is { Count: > 0 })),
        ];

        Assert.Equal(
            [("Build", "Review", implement, implement, false), ("Design", "Build", implement, implement, false), ("Design", "Review", implement, implement, true)],
            Strokes());

        shell.Click(shell.ConnectionInto("Build"));
        shell.Click(shell.Find<Button>("KindContext"));

        Assert.Contains(("Design", "Build", implement, implement, true), Strokes());
    }

    [AvaloniaFact]
    public void A_card_held_over_a_connection_covers_it()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 250),
            TaskAt(Review, "Review", 705, 90),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Dependency)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        Assert.Equal(Color.Parse("#564ADE"), shell.ColorAt(shell.Editor, new Point(535, 122)));

        // Held 180 px higher, Build's card spans y = 70 to 134, so the connection lies under its blank bottom row.
        Color? held = null;
        var header = shell.Header(shell.Node("Build"));
        shell.Drag(header, header + new Vector(0, -186), beforeRelease: () => held = shell.ColorAt(shell.Editor, new Point(535, 122)));

        Assert.Equal(Color.Parse("#FFFFFF"), held);
    }

    [AvaloniaFact]
    public void Clicking_just_beside_a_connection_selects_it()
    {
        var shell = Shell.Open(DesignThenBuild());

        shell.Click(shell.ConnectionInto("Build") + new Vector(0, 4));

        Assert.True(BaseConnection.GetIsSelected(Assert.Single(shell.Connections())));
        Assert.False(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void A_kind_change_that_would_close_a_cycle_shows_why_and_keeps_the_kind()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            TaskAt(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Review, Design), ConnectionKind.Context)));
        shell.Click(shell.ConnectionInto("Design"));
        Assert.False(shell.Find<Button>("KindContext").IsEffectivelyEnabled);

        shell.Click(shell.Find<Button>("KindDependency"));

        Assert.Equal("That would create a cycle: Review → Design → Build → Review.", shell.Status);
        Assert.Contains(("Review", "Design", ConnectionKind.Context), shell.Drawn());
        Assert.False(shell.Find<Button>("KindContext").IsEffectivelyEnabled);
        Assert.True(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Right_clicking_a_connection_opens_its_menu_which_changes_its_kind()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.RightClick(shell.ConnectionInto("Build"));
        shell.Click(shell.Window.GetVisualDescendants().OfType<MenuItem>().Single(item => (string?)item.Header == "Context"));

        Assert.Equal([("Design", "Build", ConnectionKind.Context)], shell.Drawn());
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Pressing_delete_on_a_selected_connection_removes_only_the_connection()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Click(shell.ConnectionInto("Build"));

        shell.Press(Key.Delete);

        Assert.Empty(shell.Drawn());
        Assert.Equal(["Build", "Design"], shell.Nodes().Select(node => ((TaskNodeViewModel)node.DataContext!).Title).Order());
    }

    [AvaloniaFact]
    public void Alt_clicking_a_connection_removes_it()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            TaskAt(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context)));

        shell.Click(shell.ConnectionInto("Build"), RawInputModifiers.Alt);

        Assert.Equal([("Design", "Review", ConnectionKind.Context)], shell.Drawn());
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }
}

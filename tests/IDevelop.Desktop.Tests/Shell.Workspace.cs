using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>Helpers for the sidebar's tree of open projects, their workflows, and their tasks.</summary>
internal sealed partial class Shell
{
    /// <summary>The projects as the sidebar lists them, each with its workflows' names.</summary>
    public (string Project, string[] Workflows)[] Tree() =>
        [.. ProjectItems().Select(item => (Name(item), ById<Button>(item, "WorkflowRow").Select(row => Texts(row)[0]).ToArray()))];

    /// <summary>A project's item in the sidebar: its row and its workflow rows.</summary>
    public Control ProjectItem(string project) => ProjectItems().Single(item => Name(item) == project);

    public Button WorkflowRow(string project, string workflow) => WorkflowItem(project, workflow).Row;

    /// <summary>The Nodes section's list, which lists the nodes of the shown workflow (ADR 0071).</summary>
    public ListBox WorkflowTasks(string project, string workflow)
    {
        var list = Find<ListBox>("SidebarTasks");
        return list.DataContext is Canvas.WorkflowCanvasViewModel canvas && canvas.Project.Name == project && canvas.Name == workflow
            ? list
            : throw new InvalidOperationException($"The Nodes section does not list {project} / {workflow}.");
    }

    /// <summary>The Nodes section lists the workflow's nodes.</summary>
    public bool ListsNodesOf(string project, string workflow) =>
        Find<ListBox>("SidebarTasks") is { IsEffectivelyVisible: true, DataContext: Canvas.WorkflowCanvasViewModel canvas } && canvas.Project.Name == project && canvas.Name == workflow;

    public string WorkflowText(string project, string workflow, string automationId) =>
        ById<TextBlock>(WorkflowItem(project, workflow).Item, automationId).Single().Text ?? "";

    public bool WorkflowShows(string project, string workflow, string automationId) =>
        ById<Control>(WorkflowItem(project, workflow).Item, automationId).Single().IsEffectivelyVisible;

    /// <summary>Lays out the Nodes section, which lists the shown workflow's nodes.</summary>
    public void ShowTasks() => Render();

    /// <summary>Shows the workflow, so the Nodes section lists its nodes.</summary>
    public void OpenNodes(string project, string workflow)
    {
        if (!ListsNodesOf(project, workflow))
        {
            Click(WorkflowRow(project, workflow));
        }

        Render();
    }

    /// <summary>Sets the sidebar's width, as dragging the splitter after it does, within its 240 to 400 px range.</summary>
    public void SizeSidebar(double width)
    {
        Window.FindControl<Grid>("Columns")!.ColumnDefinitions[0].Width = new GridLength(width);
        Render();
    }

    /// <summary>Opens the footer's flyout, once, and returns what it shows.</summary>
    public Control AgentsPanel()
    {
        var status = Find<Button>("AgentsStatus");
        var flyout = (Flyout)status.Flyout!;
        if (!flyout.IsOpen)
        {
            flyout.ShowAt(status);
            Render();
        }

        return (Control)flyout.Content!;
    }

    /// <summary>Runs Check Agents Again, the footer flyout's command, with the flyout closed.</summary>
    public void RefreshAgents() => Window.ViewModel.RefreshAgentsCommand.Execute(null);

    /// <summary>No check of the agents runs now.</summary>
    public bool AgentsChecked => Window.ViewModel.RefreshAgentsCommand.CanExecute(null);

    /// <summary>The project's name in its row.</summary>
    public TextBlock ProjectName(string project) => ById<TextBlock>(ProjectItem(project), "ProjectName").Single();

    /// <summary>The workflow's name in its row.</summary>
    public TextBlock WorkflowName(string project, string workflow) => ById<TextBlock>(WorkflowItem(project, workflow).Item, "WorkflowName").Single();

    /// <summary>The project, then the workflow, as the breadcrumb names them.</summary>
    public (string, string) Breadcrumb() => (Find<TextBlock>("BreadcrumbProject").Text ?? "", Find<TextBlock>("BreadcrumbWorkflow").Text ?? "");

    public ListBoxItem TaskRow(string project, string workflow, string title) =>
        WorkflowTasks(project, workflow).GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is TaskNodeViewModel node && node.Title == title);

    private IEnumerable<Control> ProjectItems() => Find<ItemsControl>("Projects").GetRealizedContainers();

    private static string Name(Control projectItem) => ById<TextBlock>(projectItem, "ProjectName").Single().Text ?? "";

    private (Control Item, Button Row) WorkflowItem(string project, string workflow)
    {
        var row = ById<Button>(ProjectItem(project), "WorkflowRow").Single(row => Texts(row)[0] == workflow);
        return (row.FindAncestorOfType<StackPanel>()!, row);
    }
}

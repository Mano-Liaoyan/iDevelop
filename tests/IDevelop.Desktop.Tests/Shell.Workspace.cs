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

    public ToggleButton WorkflowExpand(string project, string workflow) => ById<ToggleButton>(WorkflowItem(project, workflow).Item, "WorkflowExpand").Single();

    /// <summary>The workflow's task list, which shows while its row is expanded.</summary>
    public ListBox WorkflowTasks(string project, string workflow) => ById<ListBox>(WorkflowItem(project, workflow).Item, "SidebarTasks").Single();

    public string WorkflowText(string project, string workflow, string automationId) =>
        ById<TextBlock>(WorkflowItem(project, workflow).Item, automationId).Single().Text ?? "";

    public bool WorkflowShows(string project, string workflow, string automationId) =>
        ById<Control>(WorkflowItem(project, workflow).Item, automationId).Single().IsEffectivelyVisible;

    public bool ProjectShows(string project, string automationId) =>
        ById<Control>(ProjectItem(project), automationId).First().IsEffectivelyVisible;

    /// <summary>Expands the shown workflow's row, so its tasks are listed as before workflows had rows of their own.</summary>
    public void ShowTasks()
    {
        var canvas = Window.ViewModel.Canvas ?? throw new InvalidOperationException("No workflow is shown.");
        Click(WorkflowExpand(canvas.Project.Name, canvas.Name));
    }

    /// <summary>The project, then the workflow, as the breadcrumb names them.</summary>
    public (string, string) Breadcrumb() => (Find<TextBlock>("BreadcrumbProject").Text ?? "", Find<TextBlock>("BreadcrumbWorkflow").Text ?? "");

    public ListBoxItem TaskRow(string project, string workflow, string title) =>
        WorkflowTasks(project, workflow).GetVisualDescendants().OfType<ListBoxItem>().Single(row => ((TaskNodeViewModel)row.DataContext!).Title == title);

    private IEnumerable<Control> ProjectItems() => Find<ItemsControl>("Projects").GetRealizedContainers();

    private static string Name(Control projectItem) => ById<TextBlock>(projectItem, "ProjectName").Single().Text ?? "";

    private (Control Item, Button Row) WorkflowItem(string project, string workflow)
    {
        var row = ById<Button>(ProjectItem(project), "WorkflowRow").Single(row => Texts(row)[0] == workflow);
        return (row.FindAncestorOfType<ContentPresenter>()!, row);
    }
}

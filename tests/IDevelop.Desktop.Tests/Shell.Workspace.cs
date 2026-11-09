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

    /// <summary>The Index lists the shown workflow's stages under its row.</summary>
    public bool ListsNodesOf(string project, string workflow) =>
        Window.ViewModel.Canvas is { IsExpanded: true } canvas && canvas.Project.Name == project && canvas.Name == workflow;

    public string WorkflowText(string project, string workflow, string automationId) =>
        ById<TextBlock>(WorkflowItem(project, workflow).Item, automationId).Single().Text ?? "";

    public bool WorkflowShows(string project, string workflow, string automationId) =>
        ById<Control>(WorkflowItem(project, workflow).Item, automationId).Single().IsEffectivelyVisible;

    /// <summary>Lays out the Index, which lists the shown workflow's agents.</summary>
    public void ShowTasks() => Render();

    /// <summary>Shows the workflow, so the Index lists its agents.</summary>
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

    /// <summary>Runs Check Agents Again, the footer's command.</summary>
    public void RefreshAgents() => Window.ViewModel.RefreshAgentsCommand.Execute(null);

    /// <summary>No check of the agents runs now.</summary>
    public bool AgentsChecked => Window.ViewModel.RefreshAgentsCommand.CanExecute(null);

    /// <summary>The project's name in its row.</summary>
    public TextBlock ProjectName(string project) => ById<TextBlock>(ProjectItem(project), "ProjectName").Single();

    /// <summary>The workflow's name in its row.</summary>
    public TextBlock WorkflowName(string project, string workflow) => ById<TextBlock>(WorkflowItem(project, workflow).Item, "WorkflowName").Single();

    /// <summary>The project, then the workflow, as the breadcrumb names them.</summary>
    public (string, string) Breadcrumb() => (Find<TextBlock>("BreadcrumbProject").Text ?? "", Find<TextBlock>("BreadcrumbWorkflow").Text ?? "");

    public Button TaskRow(string project, string workflow, string title) =>
        Window.GetVisualDescendants().OfType<Button>().Single(row => row.Classes.Contains("agent") && row.DataContext is TaskNodeViewModel node
            && node.Title == title && node.Canvas.Name == workflow && node.Canvas.Project.Name == project);

    /// <summary>The titles of the agent rows the Index shows, from top to bottom.</summary>
    public string[] AgentRowTitles() =>
        [.. Window.GetVisualDescendants().OfType<Button>().Where(row => row.Classes.Contains("agent") && row.IsEffectivelyVisible).Select(row => ((TaskNodeViewModel)row.DataContext!).Title)];

    /// <summary>Closes a project through its row's menu, as a person does.</summary>
    public void CloseProject(string project) => ChooseInMenu(ById<Button>(ProjectItem(project), "ProjectMenu").Single(), "CloseProject");

    /// <summary>Closes the window's one open project through its row's menu.</summary>
    public void CloseOnlyProject() => CloseProject(Name(ProjectItems().Single()));

    /// <summary>Opens the Index's + menu and chooses its Open folder.</summary>
    public void OpenFolderFromIndex() => ChooseInMenu(Find<Button>("IndexAdd"), "OpenFolder");

    private void ChooseInMenu(Button menu, string item)
    {
        menu.Flyout!.ShowAt(menu);
        Render();
        ById<MenuItem>(Window, item).Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        menu.Flyout?.Hide();
        Render();
    }

    private IEnumerable<Control> ProjectItems() => Find<ItemsControl>("Projects").GetRealizedContainers();

    private static string Name(Control projectItem) => ById<TextBlock>(projectItem, "ProjectName").Single().Text ?? "";

    private (Control Item, Button Row) WorkflowItem(string project, string workflow)
    {
        var row = ById<Button>(ProjectItem(project), "WorkflowRow").Single(row => Texts(row)[0] == workflow);
        return (row.FindAncestorOfType<StackPanel>()!, row);
    }
}

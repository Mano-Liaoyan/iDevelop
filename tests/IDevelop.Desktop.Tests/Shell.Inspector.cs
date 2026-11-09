using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Tests;

/// <summary>The Agent panel's pages, fold panels, and rows.</summary>
internal sealed partial class Shell
{
    public InspectorSection Section(string key) => Find<InspectorSection>($"Section{key}");

    /// <summary>
    /// Shows the page of the Agent panel that holds the control, as a person does before using it: an item of a hidden page does not
    /// exist until the page first shows, and it takes no input before that.
    /// </summary>
    public void ShowPageOf<T>(string automationId) where T : Control
    {
        if (Window.ViewModel.Canvas?.Inspected is not TaskNodeViewModel || ById<T>(Window, automationId).Any(control => control.IsEffectivelyVisible || !InHiddenPage(control)))
        {
            return;
        }

        var start = Window.ViewModel.AgentPanel.Page;
        foreach (var page in Enum.GetValues<AgentPage>())
        {
            Window.ViewModel.AgentPanel.Show(page);
            Render();
            if (ById<T>(Window, automationId).Any(control => control.IsEffectivelyVisible))
            {
                return;
            }
        }

        Window.ViewModel.AgentPanel.Show(start);
        Render();
    }

    private static bool InHiddenPage(Control control) =>
        control.GetVisualAncestors().OfType<ScrollViewer>().Any(scroller => !scroller.IsVisible && scroller.Content is StackPanel panel && panel.Classes.Contains("page"));

    /// <summary>Gives the name's box the focus, as a double-click on the name does.</summary>
    public void EditTitle()
    {
        var shown = Find<Control>("InspectorHeader").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("headerTitle"));
        DoubleClick(Center(shown));
        Render();
    }

    /// <summary>Opens the page through its tab, as a click on the strip does.</summary>
    public void Page(AgentPage page)
    {
        Click(ById<Button>(Window, $"Page{page}").Single());
        Render();
    }

    /// <summary>Clicks the fold panel's header, which folds or unfolds it.</summary>
    public void Fold(string key)
    {
        var header = SectionHeader(key);
        header.BringIntoView();
        Render();
        Click(header);
    }

    /// <summary>The labels of the labelled rows the Agent panel shows now, in order.</summary>
    public string[] ShownRows() =>
        [.. Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>().Where(row => row is { IsEffectivelyVisible: true, Layout: not (RowLayout.Full or RowLayout.Buttons) }).Select(row => row.Label ?? "")];

    private Button SectionHeader(string key) => Section(key).GetVisualDescendants().OfType<Button>().First(button => button.Name == "PART_Header");

    /// <summary>The note that a row's info glyph holds: its short name for UI Automation and its tooltip's whole text.</summary>
    public (string? Name, object? Tip) Note(string automationId)
    {
        var glyph = Find<Control>(automationId);
        return (AutomationProperties.GetName(glyph), ToolTip.GetTip(glyph));
    }

    /// <summary>Sets the Agent panel's width, as dragging the splitter before it does.</summary>
    public void SizeInspector(double width)
    {
        // The inspector's frame holds its column.
        var frame = (Control)Find<Control>("Inspector").Parent!;
        ((Grid)frame.Parent!).ColumnDefinitions[Grid.GetColumn(frame)].Width = new GridLength(width);
        Render();
    }

    /// <summary>Shows the selected node's Chat page.</summary>
    public void Talk() => Page(Inspector.AgentPage.Chat);

    /// <summary>Opens the header's More menu and chooses the entry. A headless popup takes no clicks, so the entry raises its own.</summary>
    public void ChooseMore(string automationId)
    {
        var more = Find<Button>("InspectorMore");
        Click(more);
        var item = Window.GetVisualDescendants().OfType<Avalonia.Controls.MenuItem>().Single(menu => AutomationProperties.GetAutomationId(menu) == automationId);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        more.Flyout!.Hide();
        Render();
    }

    /// <summary>Opens the button's menu and chooses the entry, as <see cref="ChooseMore"/> does for the header's.</summary>
    public void ChooseMenu(string menu, string automationId)
    {
        var button = InView<Button>(menu);
        Click(button);
        var item = Window.GetVisualDescendants().OfType<Avalonia.Controls.MenuItem>().Single(entry => AutomationProperties.GetAutomationId(entry) == automationId);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        button.Flyout!.Hide();
        Render();
    }

}

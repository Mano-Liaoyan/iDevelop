using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Tests;

/// <summary>The inspector's sections, rows, and filter.</summary>
internal sealed partial class Shell
{
    public InspectorSection Section(string key) => Find<InspectorSection>($"Section{key}");

    /// <summary>Whether the section's header shows, which the filter hides when nothing in the section matches.</summary>
    public bool Shows(string key) => SectionHeader(key).IsEffectivelyVisible;

    /// <summary>Clicks the section's header, which folds or unfolds it.</summary>
    public void Fold(string key)
    {
        var header = SectionHeader(key);
        header.BringIntoView();
        Render();
        Click(header);
    }

    /// <summary>The labels of the labelled rows the inspector shows now, in order.</summary>
    public string[] ShownRows() =>
        [.. Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>().Where(row => row is { IsEffectivelyVisible: true, Layout: not (RowLayout.Full or RowLayout.Buttons) }).Select(row => row.Label ?? "")];

    private Button SectionHeader(string key) => Section(key).GetVisualDescendants().OfType<Button>().First(button => button.Name == "PART_Header");

    /// <summary>The note that a row's info glyph holds: its short name for UI Automation and its tooltip's whole text.</summary>
    public (string? Name, object? Tip) Note(string automationId)
    {
        var glyph = Find<Control>(automationId);
        return (AutomationProperties.GetName(glyph), ToolTip.GetTip(glyph));
    }

    /// <summary>Sets the inspector's width, as dragging the splitter before it does, within its 280 to 520 px range.</summary>
    public void SizeInspector(double width)
    {
        var inspector = Find<Control>("Inspector");
        ((Grid)inspector.Parent!).ColumnDefinitions[Grid.GetColumn(inspector)].Width = new GridLength(width);
        Render();
    }

    /// <summary>Opens the selected node's conversation from the inspector's header.</summary>
    public void Talk()
    {
        Render();
        Invoke(InView<Button>("OpenConversation"));
        Render();
    }

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

    public void FilterInspector(string text)
    {
        Click(Find<TextBox>("InspectorFilter"));
        Type(text);
    }
}

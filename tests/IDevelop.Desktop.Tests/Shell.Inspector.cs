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
        [.. Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>().Where(row => row.IsEffectivelyVisible && row.Layout is not (RowLayout.Full or RowLayout.Buttons)).Select(row => row.Label ?? "")];

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

    public void FilterInspector(string text)
    {
        Click(Find<TextBox>("InspectorFilter"));
        Type(text);
    }
}

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
        [.. Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>().Where(row => row.IsEffectivelyVisible && row.Layout != RowLayout.Full).Select(row => row.Label ?? "")];

    private Button SectionHeader(string key) => Section(key).GetVisualDescendants().OfType<Button>().First(button => button.Name == "PART_Header");

    public void FilterInspector(string text)
    {
        Click(Find<TextBox>("InspectorFilter"));
        Type(text);
    }
}

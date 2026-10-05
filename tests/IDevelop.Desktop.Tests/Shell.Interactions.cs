using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Tests;

/// <summary>Helpers for the Add popover and the canvas menus.</summary>
internal sealed partial class Shell
{
    /// <summary>Adds a node through the sidebar's Add Node button and the popover's row for the blueprint.</summary>
    public void AddNode(string blueprint = "Implement")
    {
        Click(Find<Button>("AddTask"));
        Click(AddRow(blueprint));
    }

    public bool AddPopoverIsOpen => Has<Border>("AddNodePopover") && Find<Border>("AddNodePopover").IsEffectivelyVisible;

    public Border AddPopover => Find<Border>("AddNodePopover");

    /// <summary>The popover's blueprint row for the blueprint's name.</summary>
    public Button AddRow(string blueprint) =>
        ById<Button>(Window, "AddNodeItem").Single(row => AutomationProperties.GetName(row) == $"Add {blueprint}");

    /// <summary>The one shown control with the id, where hidden rows hold more of them.</summary>
    public T Shown<T>(string automationId) where T : Control =>
        ById<T>(Window, automationId).Single(control => control.IsEffectivelyVisible);

    public bool Shows(string automationId) => ById<Control>(Window, automationId).Any(control => control.IsEffectivelyVisible);

    /// <summary>The names of the popover's rows, blueprints and actions, in order.</summary>
    public string[] AddRows() =>
        [.. Window.GetVisualDescendants().OfType<Button>()
            .Where(row => AutomationProperties.GetAutomationId(row) is "AddNodeItem" or "AddNodeAction" && row.IsEffectivelyVisible)
            .Select(row => AutomationProperties.GetName(row)!)];

    /// <summary>The name of the highlighted row.</summary>
    public string? Highlighted() =>
        Window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(row => row.Classes.Contains("highlighted") && row.IsEffectivelyVisible) is { } row ? AutomationProperties.GetName(row) : null;

    /// <summary>The canvas point under a point of the window, at the editor's zoom.</summary>
    public Point CanvasPointAt(Point inWindow)
    {
        var inEditor = Window.TranslatePoint(inWindow, Editor)!.Value;
        return Editor.ViewportLocation + (Vector)inEditor / Editor.ViewportZoom;
    }

    /// <summary>A point of the editor, given relative to its top left.</summary>
    public Point InEditor(double x, double y) => Editor.TranslatePoint(new Point(x, y), Window)!.Value;

    public void DoubleClick(Point point)
    {
        Window.MouseMove(point);
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
        Render();
    }

    /// <summary>The headers of the open menu's visible items, in order.</summary>
    public string[] MenuHeaders() =>
        [.. OpenMenuItems().Select(item => item.Header?.ToString() ?? "")];

    public MenuItem MenuItem(string automationId) => OpenMenuItems().Single(item => AutomationProperties.GetAutomationId(item) == automationId);

    public IEnumerable<MenuItem> OpenMenuItems() =>
        Window.GetVisualDescendants().OfType<ContextMenu>().Where(menu => menu.IsOpen)
            .SelectMany(menu => menu.Items.OfType<MenuItem>())
            .Where(item => item.IsVisible);

    /// <summary>Types a key that also produces text, as a keyboard does, before anything else runs.</summary>
    public void PressWithText(Key key, string text)
    {
        Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, text);
        Window.KeyTextInput(text);
        Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, text);
        Render();
    }
}

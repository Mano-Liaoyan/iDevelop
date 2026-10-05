using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the canvas view does in the inspector beside it.</summary>
public partial class WorkflowCanvasView
{
    // The inspector shows the selected node once the selection's change has run and been laid out, so the picker is found
    // after both. A folded Agent section opens, and a filter that hides the picker clears, as the person would have to do.
    // The list is placed against where the picker is when it opens, so the picker scrolls into view and is laid out first.
    // A hidden picker's list cannot open, so it opens only once the picker has the focus.
    void ICanvasView.FocusAgent() => Dispatcher.UIThread.Post(
        () =>
        {
            if (TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<InspectorView>().FirstOrDefault() is not { } inspector
                || InspectorState.GetCurrent(inspector) is not { } state)
            {
                return;
            }

            state.Fold(["Agent"], folded: false);
            inspector.UpdateLayout();
            if (ById<ComboBox>(inspector, "TaskClient") is not { } picker)
            {
                return;
            }

            if (!picker.IsEffectivelyVisible && ById<TextBox>(inspector, "InspectorFilter") is { } filter)
            {
                // The box reports its new text to the state only later.
                filter.Text = "";
                state.Filter("");
                inspector.UpdateLayout();
            }

            picker.BringIntoView();
            inspector.UpdateLayout();
            if (picker.Focus(NavigationMethod.Tab))
            {
                picker.IsDropDownOpen = true;
            }
        },
        DispatcherPriority.Background);

    private static T? ById<T>(Visual root, string automationId) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);
}

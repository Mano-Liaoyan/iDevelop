using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// A node's sections in the order that fits what the person came for. Before a node has a result, its setup comes first:
/// Task, Agent, Input, Output. Once it has one, reading it is the main task: Output, Input, Task, Agent. The children move in
/// the panel, so Tab follows the order that shows. A Proposal section stays first.
/// </summary>
public sealed class SectionStack : StackPanel
{
    public static readonly StyledProperty<bool> LeadsWithOutputProperty = AvaloniaProperty.Register<SectionStack, bool>(nameof(LeadsWithOutput));

    private static readonly string[] SetupFirst = ["Proposal", "Task", "Agent", "Input", "Output"];

    private static readonly string[] OutputFirst = ["Proposal", "Output", "Input", "Task", "Agent"];

    public bool LeadsWithOutput
    {
        get => GetValue(LeadsWithOutputProperty);
        set => SetValue(LeadsWithOutputProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LeadsWithOutputProperty)
        {
            Arrange();
        }
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Arrange();
    }

    private void Arrange()
    {
        var order = LeadsWithOutput ? OutputFirst : SetupFirst;
        var sorted = Children.OrderBy(child => Array.IndexOf(order, (child as InspectorSection)?.Key)).ToList();
        if (sorted.SequenceEqual(Children))
        {
            return;
        }

        for (var index = 0; index < sorted.Count; index++)
        {
            var at = Children.IndexOf(sorted[index]);
            if (at != index)
            {
                Children.Move(at, index);
            }
        }
    }
}

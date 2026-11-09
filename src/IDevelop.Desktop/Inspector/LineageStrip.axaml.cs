using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

/// <summary>The Room's 44 px line of neighbors. The three chips share the width, and none is wider than a third of it.</summary>
public partial class LineageStrip : UserControl
{
    private const double ChipLimit = 320, SideInset = 12, ArrowWidth = 22;

    public LineageStrip()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Before.MaxWidth = After.MaxWidth = This.MaxWidth =
            Math.Min(ChipLimit, Math.Max(0, (e.NewSize.Width - 2 * SideInset - 2 * ArrowWidth) / 3));
    }

    // One neighbor is a step. Several are a choice, so the button lists them.
    private void OnNeighbor(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not TaskNodeViewModel node)
        {
            return;
        }

        var links = button == Before ? node.Inputs : node.Outputs;
        if (links.Count > 1)
        {
            FlyoutBase.ShowAttachedFlyout(button);
        }
        else if (links.Count == 1 && links[0].Node.SelectCommand.CanExecute(null))
        {
            links[0].Node.SelectCommand.Execute(null);
        }
    }
}

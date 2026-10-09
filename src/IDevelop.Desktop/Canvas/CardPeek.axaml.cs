using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A collapsed card's peek. While it shows, its node keeps the details open, so the peek reads the task's conversation as
/// the expanded card does, and lets them go when it closes.
/// </summary>
public partial class CardPeek : UserControl
{
    /// <summary>The peek's text column: the expanded card's 296 px, so a text wraps in the peek where it wraps on the card.</summary>
    public const double PeekWidth = 296;

    private TaskNodeViewModel? _held;

    public CardPeek()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hold(DataContext as TaskNodeViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Hold(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_held is not null)
        {
            Hold(DataContext as TaskNodeViewModel);
        }
    }

    private void Hold(TaskNodeViewModel? node)
    {
        if (ReferenceEquals(node, _held))
        {
            return;
        }

        _held?.ReleaseDetail();
        _held = node;
        _held?.HoldDetail();
    }
}

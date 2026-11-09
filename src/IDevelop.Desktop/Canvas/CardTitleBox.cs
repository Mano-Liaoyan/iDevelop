using Avalonia.Controls;
using Avalonia.VisualTree;
using Nodify;

namespace IDevelop.Desktop.Canvas;

/// <summary>Renames a task on its card. After Enter or Escape, the canvas takes the keys.</summary>
public sealed class CardTitleBox : RenameBox
{
    private TaskNodeViewModel? Node => DataContext as TaskNodeViewModel;

    protected override string? ItemText => Node?.Title;

    protected override Control? FocusTarget => (Control?)this.FindAncestorOfType<NodifyEditor>() ?? this.FindAncestorOfType<FlowView>();

    protected override void End(string? text) => Node?.EndRename(text);
}

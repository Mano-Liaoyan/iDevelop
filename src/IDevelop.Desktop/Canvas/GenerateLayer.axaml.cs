using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Nodify;

namespace IDevelop.Desktop.Canvas;

public partial class GenerateLayer : UserControl
{
    private NodifyEditor? _editor;

    public GenerateLayer() => InitializeComponent();

    // Generate places its planner at the view's middle height, and only the editor knows the view's size.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _editor = this.FindAncestorOfType<WorkflowCanvasView>()?.FindControl<NodifyEditor>("Editor");
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorChanged;
            ShowViewportSize();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
            _editor = null;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ShowViewportSize();
    }

    private void OnEditorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == NodifyEditor.ViewportSizeProperty)
        {
            ShowViewportSize();
        }
    }

    private void ShowViewportSize()
    {
        if (_editor is not null && DataContext is WorkflowCanvasViewModel canvas)
        {
            canvas.ViewportSize = _editor.ViewportSize;
        }
    }
}

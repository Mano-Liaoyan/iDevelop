using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Inspector;

/// <summary>What the Agent panel shows while several nodes are selected: how many, and Delete.</summary>
public sealed class SeveralSelectedViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;

    internal SeveralSelectedViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.SelectedNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Title => $"{_canvas.SelectedNodes.Count} agents selected";

    public ICommand DeleteCommand => _canvas.DeleteSelectionCommand;
}

using System.Runtime.CompilerServices;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public sealed class TaskNodeViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private TaskDefinition _task;
    private Point _location;

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
        Inputs = [Input];
        Outputs = [Output];
    }

    public TaskId Id => _task.Id;

    public PortViewModel Input { get; }

    public PortViewModel Output { get; }

    public IReadOnlyList<PortViewModel> Inputs { get; }

    public IReadOnlyList<PortViewModel> Outputs { get; }

    public Point Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public string Title
    {
        get => _task.Title;
        set => RequestEdit(TaskField.Title, value);
    }

    public string Instructions
    {
        get => _task.Instructions;
        set => RequestEdit(TaskField.Instructions, value);
    }

    public string AcceptanceCriteria
    {
        get => _task.AcceptanceCriteria;
        set => RequestEdit(TaskField.AcceptanceCriteria, value);
    }

    internal void Update(TaskDefinition task, CanvasPoint position)
    {
        if (!ReferenceEquals(task, _task))
        {
            var old = _task;
            _task = task;
            if (old.Title != task.Title)
            {
                OnPropertyChanged(nameof(Title));
            }

            if (old.Instructions != task.Instructions)
            {
                OnPropertyChanged(nameof(Instructions));
            }

            if (old.AcceptanceCriteria != task.AcceptanceCriteria)
            {
                OnPropertyChanged(nameof(AcceptanceCriteria));
            }
        }

        Location = WorkflowCanvasViewModel.ToPoint(position);
    }

    private void RequestEdit(TaskField field, string text, [CallerMemberName] string? property = null)
    {
        if (_canvas.Edit(new WorkflowEdit.EditTask(Id, field, text)) is EditResult.Rejected)
        {
            OnPropertyChanged(property);
        }
    }
}

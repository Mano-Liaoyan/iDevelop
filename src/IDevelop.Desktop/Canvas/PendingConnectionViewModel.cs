using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public sealed class PendingConnectionViewModel(WorkflowCanvasViewModel canvas) : ObservableObject
{
    public object? Source { get; set; }

    public object? PreviewTarget
    {
        get;
        set
        {
            field = value;
            Refresh();
        }
    }

    public string PreviewText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    public bool IsRejected
    {
        get;
        private set => SetProperty(ref field, value);
    }

    private void Refresh()
    {
        var workflow = canvas.Workflow;
        if (WorkflowCanvasViewModel.ResolveEndpoints(Source, PreviewTarget) is not { } key)
        {
            PreviewText = "Drop on another task's port";
            IsRejected = false;
        }
        else if (workflow.Apply(new WorkflowEdit.Connect(key, ConnectionKind.Dependency)) is EditResult.Rejected rejected)
        {
            PreviewText = RejectionText.Describe(rejected.Reason, workflow);
            IsRejected = true;
        }
        else
        {
            PreviewText = $"{workflow.Tasks[key.To].Title} depends on {workflow.Tasks[key.From].Title}";
            IsRejected = false;
        }
    }
}

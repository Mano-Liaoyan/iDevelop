using Avalonia;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

public enum PortSide { Input, Output }

public sealed class PortViewModel(TaskNodeViewModel node, PortSide side) : ObservableObject
{
    private Point _anchor;
    private bool _isConnected;

    public TaskNodeViewModel Node { get; } = node;

    public PortSide Side { get; } = side;

    public Point Anchor
    {
        get => _anchor;
        set => SetProperty(ref _anchor, value);
    }

    // A Nodify connector only keeps its anchor current while it is connected.
    public bool IsConnected
    {
        get => _isConnected;
        internal set => SetProperty(ref _isConnected, value);
    }
}

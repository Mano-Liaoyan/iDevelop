using Avalonia;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

public enum PortSide { Input, Output }

public sealed class PortViewModel(TaskNodeViewModel node, PortSide side) : ObservableObject
{
    public TaskNodeViewModel Node { get; } = node;

    public PortSide Side { get; } = side;

    public bool IsInput => Side == PortSide.Input;

    public Point Anchor
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>What the socket's wires carry, which colors a connected socket.</summary>
    public WireFlow Flow
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsFeeding));
                OnPropertyChanged(nameof(IsStopped));
            }
        }
    } = WireFlow.Idle;

    /// <summary>A wire joins the socket into a running agent.</summary>
    public bool IsFeeding => Flow == WireFlow.Feeding;

    /// <summary>A wire joins the socket from a failed agent.</summary>
    public bool IsStopped => Flow == WireFlow.Stopped;

    /// <summary>The port's name and what passes through it.</summary>
    public string? Tip
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    // A Nodify connector only keeps its anchor current while it is connected.
    public bool IsConnected
    {
        get;
        internal set => SetProperty(ref field, value);
    }
}

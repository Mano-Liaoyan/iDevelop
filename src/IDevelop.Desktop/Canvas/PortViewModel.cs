using Avalonia;
using Avalonia.Data.Converters;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

public enum PortSide { Input, Output }

public sealed class PortViewModel(TaskNodeViewModel node, PortSide side) : ObservableObject
{
    /// <summary>The kind of the node whose port a connection being drawn starts from, so the wire takes its hue.</summary>
    public static readonly IValueConverter KindOfSource = new FuncValueConverter<object?, NodeKind?>(source => (source as PortViewModel)?.Node.Kind);

    public TaskNodeViewModel Node { get; } = node;

    public PortSide Side { get; } = side;

    public Point Anchor
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// The port's dot is filled: an input whose dependency predecessors are all completed and current, or an output with a
    /// current latest result.
    /// </summary>
    public bool IsLive
    {
        get;
        internal set => SetProperty(ref field, value);
    }

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

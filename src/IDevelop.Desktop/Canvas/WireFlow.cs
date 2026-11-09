using IDevelop.Engine;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>What a wire carries now. It sets the wire's color, weight, and motion (ADR 0045).</summary>
public enum WireFlow { Context, Feeding, Delivered, Outdated, Ready, Stopped, Idle }

public static class WireFlows
{
    /// <summary>What a wire carries. A wire into a running agent moves, whatever its kind, and a wire from a failed agent is red.</summary>
    /// <param name="kind">The connection's kind.</param>
    /// <param name="from">The source node's state.</param>
    /// <param name="to">The target node's state.</param>
    /// <param name="source">The source node's id.</param>
    /// <param name="sourceResult">The id of the source node's latest result, or null.</param>
    /// <param name="targetBasis">What the target node's latest result ran on, or null before its first result.</param>
    public static WireFlow Of(ConnectionKind kind, NodeState from, NodeState to, TaskId source, string? sourceResult, Basis? targetBasis)
    {
        if (to == NodeState.Running)
        {
            return WireFlow.Feeding;
        }

        if (from == NodeState.Failed)
        {
            return WireFlow.Stopped;
        }

        if (kind == ConnectionKind.Context)
        {
            return WireFlow.Context;
        }

        if (from != NodeState.Completed)
        {
            return WireFlow.Idle;
        }

        if (to == NodeState.InputRequired)
        {
            return WireFlow.Delivered;
        }

        if (targetBasis is null)
        {
            return WireFlow.Ready;
        }

        return targetBasis.Inputs.FirstOrDefault(input => input.Node == source)?.Result == sourceResult ? WireFlow.Delivered : WireFlow.Outdated;
    }

    /// <summary>The wire's stroke on screen. A wire into a running agent and a highlighted wire are 2 px, and every other wire is 1.5 px.</summary>
    public static double Stroke(WireFlow flow, bool highlighted) => flow == WireFlow.Feeding || highlighted ? 2 : 1.5;

    /// <summary>The flow that a socket shows when several wires join it: a wire into a running agent wins, then a wire from a failed agent.</summary>
    public static WireFlow Stronger(WireFlow current, WireFlow next) =>
        next == WireFlow.Feeding || current == WireFlow.Feeding ? WireFlow.Feeding
        : next == WireFlow.Stopped || current == WireFlow.Stopped ? WireFlow.Stopped
        : current == WireFlow.Idle ? next : current;

    /// <summary>A sentence for the wire's tooltip.</summary>
    public static string Tip(WireFlow flow, string source, string target) => flow switch
    {
        WireFlow.Context => $"Context from {source} for {target}",
        WireFlow.Feeding => $"{target} runs on the result of {source}",
        WireFlow.Delivered => $"{target} has the latest result of {source}",
        WireFlow.Outdated => $"{target} ran on an older result of {source}",
        WireFlow.Ready => $"The result of {source} is ready for {target}",
        WireFlow.Stopped => $"{source} failed, so {target} has no result to read",
        _ => $"{target} reads the result of {source}",
    };
}

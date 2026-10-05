using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>The few words a card has room for. The card's tooltip and the inspector give the full sentences.</summary>
public static class CardText
{
    /// <summary>What the person must set before the node can start, or null when the problem is not a missing setting.</summary>
    public static string? ShortReason(StartProblem problem) => problem switch
    {
        StartProblem.NoAgent => "Choose an agent",
        StartProblem.FieldMissing p => $"{p.Label} is empty",
        StartProblem.NoModel => "Choose a model",
        StartProblem.ModelNotOffered or StartProblem.ModelUnready => "Model not available",
        StartProblem.ReasoningNotOffered => "Choose a reasoning level",
        StartProblem.NoReadOnlyMode p => $"{Clients.Name(p.Client)} can't run read-only",
        StartProblem.NoSubject => "Needs a task to review",
        StartProblem.ClientMissing p => $"{Clients.Name(p.Client)} isn't installed",
        StartProblem.ClientUnready p => $"{Clients.Name(p.Client)} isn't ready",
        _ => null,
    };

    /// <summary>"Proposal · 3 tasks", the subtitle of a planner whose proposal is open.</summary>
    public static string Proposal(int tasks) => tasks == 1 ? "Proposal · 1 task" : $"Proposal · {tasks} tasks";
}

namespace IDevelop.Desktop.Canvas;

/// <summary>The few words a card has room for. The card's tooltip and the inspector give the full sentences.</summary>
public static class CardText
{
    /// <summary>"Proposal · 3 tasks", the subtitle of a planner whose proposal is open.</summary>
    public static string Proposal(int tasks) => tasks == 1 ? "Proposal · 1 task" : $"Proposal · {tasks} tasks";
}

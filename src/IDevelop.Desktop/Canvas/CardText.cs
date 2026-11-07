using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>The few words a card has room for. The card's tooltip and the inspector give the full sentences.</summary>
public static class CardText
{
    /// <summary>The first <paramref name="limit"/> characters of <paramref name="text"/> on one line, with an ellipsis when it loses the rest.</summary>
    public static string Line(string text, int limit)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= limit ? line : line[..limit].TrimEnd() + "…";
    }

    /// <summary>"Proposal · 3 tasks", the subtitle of a planner whose proposal is open.</summary>
    public static string Proposal(int tasks) => tasks == 1 ? "Proposal · 1 task" : $"Proposal · {tasks} tasks";

    /// <summary>The two letters that stand for a client where its name has no room.</summary>
    public static string ClientMark(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "CC",
        ClientId.Codex => "Cx",
        ClientId.Pi => "Pi",
        _ => "AG",
    };
}

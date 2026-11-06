using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>The few words a card has room for. The card's tooltip and the inspector give the full sentences.</summary>
public static class CardText
{
    /// <summary>"Proposal · 3 tasks", the subtitle of a planner whose proposal is open.</summary>
    public static string Proposal(int tasks) => tasks == 1 ? "Proposal · 1 task" : $"Proposal · {tasks} tasks";

    /// <summary>The letter that stands for a client where its name has no room.</summary>
    public static string ClientMark(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "C",
        ClientId.Codex => "X",
        ClientId.Pi => "P",
        _ => "A",
    };

    /// <summary>A zoomed out card's model lines: a model's card lines, and last its family, which fits whole where its version does not.</summary>
    public static IReadOnlyList<string> FarLines(IReadOnlyList<string> lines)
    {
        var words = lines[^1].Split(' ');
        return [.. lines.Append(words.Length > 1 ? string.Join(' ', words[..^1]) : lines[^1]).Distinct()];
    }

    /// <summary>A model's card lines, the fullest first, down to the words that name its version.</summary>
    public static IReadOnlyList<string> ModelLines(string full, string brief)
    {
        var model = brief.Split(" · ")[0];
        var words = model.Split(' ');
        var version = Array.FindIndex(words, word => word.Any(char.IsDigit));
        return [.. new[] { full, brief, model, version < 0 ? model : string.Join(' ', words[..(version + 1)]) }.Distinct()];
    }
}

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

    /// <summary>A zoomed out card's model lines: a model's card lines, and last its family, or its major version, which fits whole where the rest does not.</summary>
    public static IReadOnlyList<string> FarLines(IReadOnlyList<string> lines)
    {
        var last = lines[^1];
        var words = last.Split(' ');
        var family = words.Length > 1 ? string.Join(' ', words[..^1]) : last.Contains('.') ? last[..last.LastIndexOf('.')] : last;
        return [.. lines.Append(family).Distinct()];
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

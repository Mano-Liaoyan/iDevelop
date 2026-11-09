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

    /// <summary>The two letters that stand for a client where its name has no room.</summary>
    public static string ClientMark(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "CC",
        ClientId.Codex => "Cx",
        ClientId.Pi => "Pi",
        _ => "AG",
    };

    /// <summary>
    /// A card's model text: the model's display name without the leading family word that the client's badge already gives
    /// ("Claude" beside CC, "Gemini" beside AG), so the words that tell models apart show. It is the end of the full name and
    /// never cuts a word. A card too narrow for it ends it in an ellipsis (ADR 0063).
    /// </summary>
    public static string ModelName(ClientId client, string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Length > 1 && words[0] == Family(client) ? words[1..] : words);
    }

    /// <summary>The model family that a client's own maker names, which its badge stands for.</summary>
    private static string? Family(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "Claude",
        ClientId.Antigravity => "Gemini",
        _ => null,
    };
}

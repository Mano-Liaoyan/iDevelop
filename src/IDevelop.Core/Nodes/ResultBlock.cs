using System.Text.Json;
using System.Text.RegularExpressions;

namespace IDevelop.Nodes;

/// <summary>
/// The fenced block an agent ends its final message with to tell iDevelop something: a question, and later a proposal
/// or a verdict. One parser for every client, because Pi has no schema flag. The block is the last thing in the message:
/// <code>
/// ```idevelop
/// {"status": "asking", "question": "..."}
/// ```
/// </code>
/// </summary>
public abstract partial record ResultBlock
{
    private ResultBlock() { }

    /// <summary>The message does not end with a block.</summary>
    public sealed record Absent : ResultBlock;

    /// <summary>The message ends with a block that is not a JSON object. The problem is for the user.</summary>
    public sealed record Unreadable(string Problem) : ResultBlock;

    /// <summary>The block's JSON object. <see cref="Status"/> is its "status" text, if any.</summary>
    public sealed record Readable(JsonElement Value) : ResultBlock
    {
        public string? Status => Text("status");

        public string? Text(string property) =>
            Value.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static ResultBlock Read(string? finalText) => Find(finalText).Block;

    /// <summary>
    /// The message as the person reads it. A block iDevelop read leaves the message, because the question, the proposal,
    /// or the verdict has its own place. An unreadable block stays, so the person sees what went wrong.
    /// </summary>
    public static string Prose(string? finalText) => Find(finalText) switch
    {
        (Readable, var text, var start) => text[..start].TrimEnd(),
        _ => finalText ?? "",
    };

    private static (ResultBlock Block, string Text, int Start) Find(string? finalText)
    {
        var text = finalText?.Replace("\r\n", "\n").TrimEnd() ?? "";
        var start = text.LastIndexOf("```idevelop", StringComparison.Ordinal);
        if (start < 0)
        {
            return (new Absent(), text, start);
        }

        if (Fence().Match(text[start..]) is not { Success: true } match)
        {
            // A block that never closes was cut off. One that closes before more text is not the message's end.
            return (text.IndexOf("\n```", start, StringComparison.Ordinal) < 0
                ? new Unreadable("The block has no closing fence.")
                : new Absent(), text, start);
        }

        try
        {
            using var json = JsonDocument.Parse(match.Groups["json"].Value);
            return (json.RootElement.ValueKind == JsonValueKind.Object
                ? new Readable(json.RootElement.Clone())
                : new Unreadable("The block holds JSON that is not an object."), text, start);
        }
        catch (JsonException e)
        {
            return (new Unreadable($"The block is not valid JSON. {e.Message}"), text, start);
        }
    }

    [GeneratedRegex(@"\A```idevelop[ \t]*\n(?<json>[\s\S]*?)\n```\z")]
    private static partial Regex Fence();
}

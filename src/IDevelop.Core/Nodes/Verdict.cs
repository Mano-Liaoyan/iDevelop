using System.Collections.Immutable;
using System.Text.Json;

namespace IDevelop.Nodes;

/// <summary>
/// A finding as the reviewer lists it in a verdict: still standing, with the change that would settle it, its severity in
/// lower case, such as "high", and its location, such as "src/Calc.cs:12". Either is null when the reviewer gave no text.
/// </summary>
public sealed record Standing(string Id, string Text, string Change, string? Severity = null, string? Location = null);

/// <summary>The reviewer's verdict block. Approval lists no finding, and a request for changes lists at least one.</summary>
public sealed record Verdict(bool Approves, ImmutableArray<Standing> Findings)
{
    /// <summary>The verdict at the end of a reviewer's final message, or why it cannot be read.</summary>
    public static (Verdict? Verdict, string? Problem) Read(string? finalText)
    {
        var read = ResultBlock.Read(finalText);
        if (read is not ResultBlock.Readable block)
        {
            return (null, read is ResultBlock.Unreadable unreadable
                ? unreadable.Problem
                : "The message does not end with a verdict block.");
        }

        if (block.Status != "verdict")
        {
            return (null, "The block's status is not \"verdict\".");
        }

        var approves = block.Text("verdict") switch
        {
            "approve" => true,
            "changes" => false,
            _ => (bool?)null,
        };
        if (approves is null)
        {
            return (null, "The verdict is neither \"approve\" nor \"changes\".");
        }

        var standing = ImmutableArray.CreateBuilder<Standing>();
        foreach (var item in Items(block.Value, "findings"))
        {
            if (Id(item) is not { } id)
            {
                return (null, "A finding has no id.");
            }

            if (Text(item, "text") is not { } text || Text(item, "change") is not { } change)
            {
                return (null, $"Finding {id} needs its text and the change that would settle it.");
            }

            if (standing.Any(other => other.Id == id))
            {
                return (null, $"Finding {id} appears twice.");
            }

            // A severity or a location that is missing or not text is left out, so a verdict that gets them wrong still reads.
            standing.Add(new Standing(id, text, change, Text(item, "severity")?.ToLowerInvariant(), Text(item, "location")));
        }

        return (approves.Value, standing.Count) switch
        {
            (true, > 0) => (null, "The verdict approves but lists findings that still stand."),
            (false, 0) => (null, "The verdict asks for changes but lists no finding."),
            _ => (new Verdict(approves.Value, standing.ToImmutable()), null),
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement block, string property) =>
        block.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    /// <summary>A string id, or a number written as one, since agents number their findings.</summary>
    private static string? Id(JsonElement item) => item.TryGetProperty("id", out var id) ? id.ValueKind switch
    {
        JsonValueKind.String when !string.IsNullOrWhiteSpace(id.GetString()) => id.GetString()!.Trim(),
        JsonValueKind.Number => id.GetRawText(),
        _ => null,
    } : null;

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}

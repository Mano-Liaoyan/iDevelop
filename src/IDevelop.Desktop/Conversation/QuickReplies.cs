using System.Text.RegularExpressions;
using IDevelop.Desktop.Execution;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// Finds the explicit options of a question an agent asks in its text: a Markdown bullet or numbered list that is the
/// message's last list, with a question beside it. The result is only what the composer offers. The engine and the node's
/// log never see it, and a message with no such list offers nothing.
/// </summary>
internal static partial class QuickReplies
{
    /// <summary>The most options a list may have: one per digit key. A longer list reads as content, not as a choice.</summary>
    internal const int MostOptions = 9;

    private enum BlockKind { Paragraph, Bullets, Numbers, Other }

    private sealed record Block(BlockKind Kind, List<string> Lines);

    /// <summary>The list's options in order, without their markers and inline Markdown, or none.</summary>
    internal static IReadOnlyList<string> Read(string? message)
    {
        var blocks = Blocks(message ?? "");
        var last = blocks.Count - 1;
        var (list, lead, close) = last >= 0 && IsList(blocks[last]) ? (last, last - 1, -1) : last >= 1 && IsList(blocks[last - 1]) ? (last - 1, last - 2, last) : (-1, -1, -1);
        if (list < 0 || !Asks(lead >= 0 ? blocks[lead] : null, close >= 0 ? blocks[close] : null))
        {
            return [];
        }

        var options = blocks[list].Lines;
        return options.Count is >= 2 and <= MostOptions && options.All(option => option.Length > 0 && !CheckBox().IsMatch(option))
            ? [.. options.Select(ResultViewModel.Plain)]
            : [];
    }

    private static bool IsList(Block block) => block.Kind is BlockKind.Bullets or BlockKind.Numbers;

    // The line before the list asks or names a choice, or the paragraph after the list asks which of them to take. "I changed:" and
    // "Shall I go on?" after a list of changes offer nothing.
    private static bool Asks(Block? lead, Block? close)
    {
        if (close is not null && !(close.Kind == BlockKind.Paragraph && Ending(close) == '?' && NamesChoice(close)))
        {
            return false;
        }

        return close is not null || lead is { Kind: BlockKind.Paragraph } && (Ending(lead) == '?' || Ending(lead) == ':' && NamesChoice(lead));
    }

    private static char Ending(Block paragraph) => paragraph.Lines[^1].TrimEnd(' ', '*', '_', ')', '"', '\'') is { Length: > 0 } line ? line[^1] : '\0';

    private static bool NamesChoice(Block paragraph) => ChoiceWord().IsMatch(paragraph.Lines[^1]);

    private static List<Block> Blocks(string message)
    {
        List<Block> blocks = [];
        var fenced = false;
        var afterBlank = true;
        foreach (var line in message.Split('\n').Select(text => text.TrimEnd('\r')))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                if (fenced) blocks.Add(new Block(BlockKind.Other, []));
                afterBlank = false;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (line.Trim().Length == 0)
            {
                afterBlank = true;
                continue;
            }

            var item = Item().Match(line);
            var open = blocks.Count > 0 ? blocks[^1] : null;
            if (item.Success)
            {
                var kind = char.IsDigit(item.Groups["marker"].Value[0]) ? BlockKind.Numbers : BlockKind.Bullets;
                if (open?.Kind != kind) blocks.Add(open = new Block(kind, []));
                open.Lines.Add(item.Groups["text"].Value.Trim());
            }
            else if (open is not null && IsList(open) && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // An indented line belongs to the item above it: its detail, or a nested list.
            }
            else if (open?.Kind == BlockKind.Paragraph && !afterBlank)
            {
                open.Lines.Add(line.Trim());
            }
            else
            {
                blocks.Add(new Block(line.TrimStart().StartsWith('|') || line.TrimStart().StartsWith('#') ? BlockKind.Other : BlockKind.Paragraph, [line.Trim()]));
            }

            afterBlank = false;
        }

        return blocks;
    }

    [GeneratedRegex(@"^ ?(?<marker>[-*+]|\d{1,2}[.)])\s+(?<text>\S.*)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"^\[[ xX]\]")]
    private static partial Regex CheckBox();

    [GeneratedRegex(@"\b(which|choose|pick|select|prefer|options?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChoiceWord();
}

/// <summary>One option of a question in the agent's text, which fills the composer. It never sends.</summary>
public sealed class QuickReplyViewModel(int number, string text)
{
    /// <summary>The option's place in the list, which the digit key picks.</summary>
    public int Number { get; } = number;

    public string Digit => $"{Number}";

    public string Text { get; } = text;
}

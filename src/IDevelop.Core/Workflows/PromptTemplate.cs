using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace IDevelop.Workflows;

/// <summary>
/// A prompt template. <c>{{name}}</c> inserts a value without its surrounding white space. <c>{{#name}}</c> ...
/// <c>{{/name}}</c> keeps its text only when the value is not blank. Every other character is literal, including braces
/// that form no tag, so a template can quote code.
/// </summary>
public sealed partial class PromptTemplate : IEquatable<PromptTemplate>
{
    private readonly ImmutableArray<Part> _parts;

    private PromptTemplate(string text, ImmutableArray<Part> parts, ImmutableSortedSet<string> variables)
    {
        Text = text;
        _parts = parts;
        Variables = variables;
    }

    public string Text { get; }

    /// <summary>Every name the template reads, in values and in sections.</summary>
    public ImmutableSortedSet<string> Variables { get; }

    /// <exception cref="FormatException">A section is not closed, or a close tag does not match its section. The message is for the user.</exception>
    public static PromptTemplate Parse(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var root = ImmutableArray.CreateBuilder<Part>();
        var open = new Stack<(string Name, ImmutableArray<Part>.Builder Outer)>();
        var current = root;
        var variables = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var at = 0;
        foreach (Match tag in Tag().Matches(text))
        {
            if (tag.Index > at)
            {
                current.Add(new Part.Literal(text[at..tag.Index]));
            }

            at = tag.Index + tag.Length;
            var name = tag.Groups["name"].Value;
            variables.Add(name);
            switch (tag.Groups["mark"].Value)
            {
                case "#":
                    open.Push((name, current));
                    current = ImmutableArray.CreateBuilder<Part>();
                    break;
                case "/":
                    if (!open.TryPop(out var section))
                    {
                        throw new FormatException($"{{{{/{name}}}}} closes no section.");
                    }

                    if (section.Name != name)
                    {
                        throw new FormatException($"{{{{/{name}}}}} closes {{{{#{section.Name}}}}}.");
                    }

                    section.Outer.Add(new Part.Section(name, current.ToImmutable()));
                    current = section.Outer;
                    break;
                default:
                    current.Add(new Part.Value(name));
                    break;
            }
        }

        if (open.TryPeek(out var unclosed))
        {
            throw new FormatException($"{{{{#{unclosed.Name}}}}} has no {{{{/{unclosed.Name}}}}}.");
        }

        if (at < text.Length)
        {
            current.Add(new Part.Literal(text[at..]));
        }

        return new PromptTemplate(text, root.ToImmutable(), variables.ToImmutable());
    }

    /// <param name="value">The value of each name in <see cref="Variables"/>.</param>
    public string Render(Func<string, string> value)
    {
        var text = new StringBuilder();
        Render(_parts, value, text);
        return text.ToString();
    }

    public bool Equals(PromptTemplate? other) => other is not null && Text == other.Text;

    public override bool Equals(object? obj) => Equals(obj as PromptTemplate);

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Text;

    private static void Render(ImmutableArray<Part> parts, Func<string, string> value, StringBuilder text)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case Part.Literal literal:
                    text.Append(literal.Text);
                    break;
                case Part.Value v:
                    text.Append(value(v.Name).Trim());
                    break;
                case Part.Section section when !string.IsNullOrWhiteSpace(value(section.Name)):
                    Render(section.Parts, value, text);
                    break;
            }
        }
    }

    [GeneratedRegex(@"\{\{(?<mark>[#/]?)(?<name>[A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex Tag();

    private abstract record Part
    {
        public sealed record Literal(string Text) : Part;

        public sealed record Value(string Name) : Part;

        public sealed record Section(string Name, ImmutableArray<Part> Parts) : Part;
    }
}

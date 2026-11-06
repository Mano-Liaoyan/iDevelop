using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// Splits a code block into runs that its styles color: comments, strings, and keywords. It reads no grammar, so it stays a
/// quiet aid to the eye for any named language, and a block with no language, or a very long one, keeps its plain text.
/// </summary>
internal static partial class CodeColors
{
    private const int LongestBlock = 20_000;

    private static readonly HashSet<string> HashComments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "dockerfile", "make", "py", "python", "rb", "ruby", "sh", "shell", "toml", "yaml", "yml", "zsh",
    };

    /// <summary>The runs of <paramref name="code"/>, or null when it stays plain.</summary>
    public static IReadOnlyList<Inline>? Runs(string code, string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || code.Length > LongestBlock)
        {
            return null;
        }

        var hash = HashComments.Contains(language.Trim());
        var runs = new List<Inline>();
        var at = 0;
        foreach (Match token in Tokens().Matches(code))
        {
            var style = token.Groups["comment"].Success ? "codeComment"
                : token.Groups["hash"].Success ? (hash ? "codeComment" : null)
                : token.Groups["text"].Success ? "codeString"
                : "codeKeyword";
            if (style is null)
            {
                continue;
            }

            if (token.Index > at)
            {
                runs.Add(new Run(code[at..token.Index]));
            }

            var run = new Run(token.Value);
            run.Classes.Add(style);
            runs.Add(run);
            at = token.Index + token.Length;
        }

        if (at < code.Length)
        {
            runs.Add(new Run(code[at..]));
        }

        return runs;
    }

    [GeneratedRegex("""
        (?<comment>//[^\n]*|/\*[\s\S]*?\*/|--\s[^\n]*)
        |(?<hash>(?<![\w&])\#[^\n]*)
        |(?<text>"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)
        |\b(?:abstract|as|async|await|bool|break|case|catch|class|const|continue|def|default|defer|do|elif|else|enum|except|export|extends|false|fi|final|finally|fn|for|from|func|function|if|impl|implements|import|in|int|interface|internal|is|let|match|mut|namespace|new|nil|none|None|not|null|or|override|package|private|protected|pub|public|readonly|record|return|sealed|select|self|static|string|struct|super|switch|then|this|throw|trait|true|True|False|try|type|typeof|use|using|var|void|where|while|with|yield|SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|JOIN|CREATE|TABLE|ALTER|INDEX|AND|OR)\b
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Tokens();
}

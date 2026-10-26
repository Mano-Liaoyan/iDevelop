using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using IDevelop.Engine;
using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>What the conversation tests read: a markdown block's text, and a scripted conversation.</summary>
internal static class ConversationFixtures
{
    public static readonly ExecutionSettings CodexHigh = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    /// <summary>The text a markdown block shows, with its runs and links in order.</summary>
    public static string Shown(TextBlock text) => text.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.Select(Shown)) : text.Text ?? "";

    public static string[] Blocks(Control root) =>
        [.. root.GetVisualDescendants().OfType<SelectableTextBlock>().Where(text => text.IsEffectivelyVisible).Select(Shown)];

    private static string Shown(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(Shown)),
        InlineUIContainer { Child: Button { Content: TextBlock label } } => label.Text ?? "",
        _ => "",
    };
}

/// <summary>A node's conversation whose lines the test writes.</summary>
internal sealed class FakeConversation(TaskId node) : IConversation
{
    public TaskId Node { get; } = node;

    public ImmutableList<ConversationLine> Lines { get; private set; } = [];

    public string? TerminalCommand { get; set; }

    public ImmutableArray<BackgroundTask> Background { get; set; } = [];

    public bool Disposed { get; private set; }

    public event Action? Changed;

    public void Add(params ConversationLine[] lines)
    {
        Lines = Lines.AddRange(lines);
        Changed?.Invoke();
    }

    public void Dispose() => Disposed = true;
}

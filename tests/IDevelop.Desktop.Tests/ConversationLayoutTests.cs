using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Conversation;
using IDevelop.TestSupport;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A run task's conversation over the canvas, at the smallest and a large window and with the inspector at its least,
/// default, and greatest width: its header, the run's strip, and the composer keep every control whole and apart, and the
/// first message folds iDevelop's artifact instructions into a one-line note.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class ConversationLayoutTests
{
    private static readonly (double Width, double Height, double Inspector)[] Sizes =
    [
        (900, 600, 280), (900, 600, 320), (900, 600, 520),
        (1600, 1000, 280), (1600, 1000, 320), (1600, 1000, 520),
    ];

    [AvaloniaFact]
    public void The_header_the_runs_strip_and_the_composer_keep_every_control_whole_and_apart_at_every_size()
    {
        using var f = FanOut.Fixture();
        var shell = Opened(f);

        foreach (var (width, height, inspector) in Sizes)
        {
            shell.Window.Width = width;
            shell.Window.Height = height;
            shell.Render();
            shell.SizeInspector(inspector);
            var at = $"{width}x{height}, inspector {inspector}";
            var view = shell.Bounds(shell.Find<Control>("ConversationView"));

            var header = shell.Bounds(Shell.Around(shell.Find<TextBlock>("ConversationTitle"), "conversationHeader"));
            AssertWholeAndApart(shell, at, header.Intersect(view), 12,
                ("Title", shell.Find<Control>("ConversationTitle")), ("Status", Shell.Around(shell.Find<TextBlock>("ConversationStatus"), "pill")),
                ("Attempt", shell.Find<Control>("AttemptPicker")), ("Layout", shell.Find<Control>("ConversationLayout")),
                ("Close", shell.Find<Control>("CloseConversation")));
            Assert.Equal(header.Right - 12, shell.Bounds(shell.Find<Control>("CloseConversation")).Right, 0.5);

            var strip = shell.Bounds(Shell.Around(shell.Find<TextBlock>("ConversationRunStatus"), "limitations"));
            // The status, its progress, and its activity wrap as one group, its lines 4 px apart.
            AssertWholeAndApart(shell, at, strip.Intersect(view), 16, 4,
                ("Run status", Shell.Around(shell.Find<TextBlock>("ConversationRunStatus"), "pill")), ("Progress", shell.Find<Control>("ConversationRunProgress")),
                ("Activity", shell.Find<Control>("ConversationRunActivity")));
            AssertWholeAndApart(shell, at, strip.Intersect(view), 16,
                ("Run", (Control)shell.Find<Control>("ConversationRunProgress").GetVisualParent()!), ("Stop", shell.Find<Control>("ConversationStopWorkflow")));

            var composer = shell.Bounds(Shell.Around(shell.Find<TextBox>("ConversationComposer"), "composer"));
            Assert.True(view.Contains(composer), $"At {at}, the composer at {composer} leaves the view at {view}.");
            AssertWholeAndApart(shell, at, composer, 10,
                ("Hint", shell.Find<Control>("ComposerHint")), ("Mark done", shell.Find<Control>("ConversationMarkDone")),
                ("Cancel", shell.Find<Control>("ConversationCancel")), ("Send", shell.Find<Control>("ConversationSend")));
            // Send ends where the composer's box ends.
            Assert.Equal(shell.Bounds(shell.Find<Control>("ConversationComposer")).Right, shell.Bounds(shell.Find<Control>("ConversationSend")).Right, 0.5);
        }
    }

    [AvaloniaFact]
    public void The_first_message_folds_idevelops_artifact_instructions_into_a_note_that_opens_on_a_click()
    {
        using var f = FanOut.Fixture();
        var shell = Opened(f);
        var prompt = shell.Window.ViewModel.Conversation!.Items.OfType<MessageItemViewModel>().First();

        Assert.Equal("iDevelop", prompt.AuthorLabel);
        Assert.StartsWith("# Write docs", prompt.Text);
        Assert.DoesNotContain("Declare artifacts", prompt.Text);
        Assert.StartsWith("Declare artifacts in .idp/outbox/", prompt.Instructions);
        var message = Shell.ById<Control>(shell.Window, "Message").First();
        Assert.DoesNotContain(Shell.Texts(message), text => text.Contains(".idp/outbox", StringComparison.Ordinal));

        var note = shell.Shown<ToggleButton>("ArtifactInstructions");
        Assert.Equal(("Artifact instructions from iDevelop", false), (AutomationProperties.GetName(note), ShowsText(shell)));
        Assert.Equal(["Artifact instructions from iDevelop"], Shell.Texts(note));
        Assert.True(shell.Bounds(note).Height <= 24, $"The note is {shell.Bounds(note).Height} px tall, more than one line.");

        shell.Click(note);

        Assert.Equal(prompt.Instructions, Shell.ById<SelectableTextBlock>(shell.Window, "ArtifactInstructionsText").Single(text => text.IsEffectivelyVisible).Text);
        Assert.True(ShowsText(shell));
        shell.Click(note);
        Assert.False(ShowsText(shell));
    }

    private static bool ShowsText(Shell shell) => shell.ShowsAny("ArtifactInstructionsText");

    /// <summary>Runs the fan-out and opens "Write docs"'s conversation, which waits for the person, over the canvas.</summary>
    private static Shell Opened(WorkflowRunFixture f)
    {
        var shell = f.Window();
        FanOut.Run(shell);
        shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));
        var conversation = shell.Window.ViewModel.Conversation!;
        shell.WaitUntil(() => conversation.SelectedAttempt is not null && conversation.Items.OfType<MessageItemViewModel>().Count() >= 2, "the conversation shows its turn");
        Assert.True(shell.Find<Control>("ConversationRun").IsEffectivelyVisible);
        return shell;
    }

    /// <summary>
    /// Each control lies inside the area, <paramref name="inset"/> from its left and right edges, at least 8 px, or
    /// <c>gap</c>, from every other, and none of its text is cut short.
    /// </summary>
    private static void AssertWholeAndApart(Shell shell, string at, Rect area, double inset, params (string Name, Control Control)[] controls) =>
        AssertWholeAndApart(shell, at, area, inset, 8, controls);

    private static void AssertWholeAndApart(Shell shell, string at, Rect area, double inset, double gap, params (string Name, Control Control)[] controls)
    {
        var shown = controls.Where(control => control.Control.IsEffectivelyVisible).Select(control => (control.Name, Box: shell.Bounds(control.Control), control.Control)).ToArray();
        var room = new Rect(area.Left + inset, area.Top, area.Width - 2 * inset, area.Height);
        foreach (var (name, box, control) in shown)
        {
            Assert.True(room.Inflate(0.5).Contains(box), $"At {at}, {name} at {box} leaves {room}.");
            var cut = control.GetSelfAndVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.TextLayout.TextLines.Any(line => line.HasCollapsed));
            Assert.Empty(cut.Select(text => $"At {at}, {name}'s \"{text.Text}\" is cut short."));
        }

        foreach (var (first, second) in shown.SelectMany((a, index) => shown.Skip(index + 1).Select(b => (a, b))))
        {
            Assert.False(first.Box.Inflate(gap / 2 - 0.5).Intersects(second.Box.Inflate(gap / 2 - 0.5)),
                $"At {at}, {first.Name} at {first.Box} and {second.Name} at {second.Box} are less than {gap} px apart.");
        }
    }
}

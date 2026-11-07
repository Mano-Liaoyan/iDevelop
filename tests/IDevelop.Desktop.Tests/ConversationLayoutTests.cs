using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Conversation;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The conversation of a task that ran, over the canvas, at the smallest and a large window and with the inspector at its least,
/// default, and greatest width: its header, the bar's strip, and the composer keep every control whole and apart, and the
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
            AssertWholeAndApart(shell, at, header.Intersect(view), 12, 4,
                ("Title", shell.Find<Control>("ConversationTitle")), ("Next", shell.Find<Control>("ConversationNext")), ("Stop", shell.Find<Control>("ConversationStopAll")),
                ("Layout", shell.Find<Control>("ConversationLayout")), ("Close", shell.Find<Control>("CloseConversation")));
            Assert.Equal(header.Right - 12, shell.Bounds(shell.Find<Control>("CloseConversation")).Right, 0.5);

            var composer = shell.Bounds(Shell.Around(shell.Find<TextBox>("ConversationComposer"), "composer"));
            Assert.True(view.Contains(composer), $"At {at}, the composer at {composer} leaves the view at {view}.");
            AssertWholeAndApart(shell, at, composer, 8, 4,
                ("Model", shell.Find<Control>("ConversationModel")), ("More", shell.Find<Control>("ConversationMore")), ("Send", shell.Find<Control>("ConversationSend")));
            // The composer's row ends at its own padding, and the card over it holds what the person can do.
            var card = shell.Bounds(shell.Find<Control>("ConversationAttention"));
            Assert.True(view.Contains(card) && card.Bottom <= composer.Top, $"At {at}, the card at {card} leaves the view, or meets the composer at {composer}.");
            Assert.True(card.Contains(shell.Bounds(shell.Find<Control>("ConversationApprove"))), $"At {at}, Approve leaves the card.");
        }
    }

    [AvaloniaFact]
    public void A_reply_shows_its_card_over_the_composer_in_the_main_area_and_the_dock_and_Approve_works_from_it()
    {
        using var f = FanOut.Fixture();
        var shell = Opened(f);
        var window = shell.Window.ViewModel;
        var model = window.Conversation!;

        foreach (var docked in new[] { false, true })
        {
            window.IsConversationDocked = docked;
            shell.Render();
            Assert.Equal((AttentionCard.Reply, "Reply", true), (model.Card, model.CardTitle, model.IsCompactCard));
            Assert.Equal("Reply to Antigravity CLI", model.Watermark);
            var card = shell.Bounds(shell.Find<Control>("ConversationAttention"));
            var composer = shell.Bounds(Shell.Around(shell.Find<TextBox>("ConversationComposer"), "composer"));
            Assert.Equal(12, composer.Top - card.Bottom, 0.5);
        }

        Assert.True(model.Items.OfType<MarkerItemViewModel>().Last().IsHeldByCard);
        Shell.Invoke(shell.Find<Button>("ConversationApprove"));

        shell.WaitForState("Write docs", Canvas.NodeState.Completed);
        Assert.Equal(AttentionCard.None, model.Card);
        Assert.DoesNotContain(model.Items.OfType<MarkerItemViewModel>(), marker => marker.IsHeldByCard);
    }

    /// <summary>Runs the fan-out and opens "Write docs"'s conversation, which waits for the person, over the canvas.</summary>
    private static Shell Opened(NodeFixture f)
    {
        var shell = f.Open();
        FanOut.Run(shell);
        shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));
        var conversation = shell.Window.ViewModel.Conversation!;
        shell.WaitUntil(() => conversation.Items.OfType<MessageItemViewModel>().Any(), "the conversation shows its turn");
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
        // A control that gives way when its row spills keeps no room.
        var shown = controls.Where(control => control.Control.IsEffectivelyVisible && control.Control.Bounds.Width > 0)
            .Select(control => (control.Name, Box: shell.Bounds(control.Control), control.Control)).ToArray();
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

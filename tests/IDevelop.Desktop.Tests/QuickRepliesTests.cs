using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.AgyTurn;

namespace IDevelop.Desktop.Tests;

/// <summary>A question that lists its options in its text offers them over the composer. An option fills the composer and never sends.</summary>
[Collection(ProcessCollection.Name)]
public sealed class QuickRepliesTests
{
    [Theory]
    [InlineData("Which window should I use?\n\n- The longer one\n- Each provider's own", "The longer one|Each provider's own")]
    [InlineData("Which window should I use?\n1. The longer one\n2. Each provider's own", "The longer one|Each provider's own")]
    [InlineData("Which window should I use?\r\n* One\r\n* Two\r\n", "One|Two")]
    [InlineData("How?\n\n1) One\n\n2) Two", "One|Two")]
    [InlineData("Please choose one:\n+ One\n+ Two", "One|Two")]
    [InlineData("The two ways:\n\n- One\n- Two\n\nWhich do you prefer?", "One|Two")]
    [InlineData("How?\n- **Longer** window, as `7d`\n- Own window", "Longer window, as 7d|Own window")]
    [InlineData("How?\n- One\n  with detail\n  - nested\n- Two", "One|Two")]
    public void An_explicit_list_beside_a_question_gives_its_options(string message, string expected) =>
        Assert.Equal(expected.Split('|'), QuickReplies.Read(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Should the window follow the longer one, or should each provider keep its own?")]
    [InlineData("I changed these files:\n\n- a.cs\n- b.cs")]
    [InlineData("I changed these files:\n\n- a.cs\n- b.cs\n\nShall I go on?")]
    [InlineData("Which one?\n- Only")]
    [InlineData("What is left?\n- [ ] One\n- [x] Two")]
    [InlineData("Which one?\n1. a\n2. b\n3. c\n4. d\n5. e\n6. f\n7. g\n8. h\n9. i\n10. j")]
    [InlineData("Which one?\n```\n- One\n- Two\n```")]
    [InlineData("Which one?\n- One\n- Two\n\nI will start after lunch.")]
    public void Anything_else_gives_no_options(string? message) => Assert.Empty(QuickReplies.Read(message));

    private static NodeFixture Asking(string reply)
    {
        var f = new NodeFixture(NodeFixture.Task(TestTasks.Design, "Choose", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", reply));
        return f;
    }

    private static (Shell Shell, ConversationViewModel Model) Waiting(NodeFixture f)
    {
        var shell = f.Open();
        var node = shell.NodeModel("Choose");
        node.RunCommand.Execute(null);
        shell.WaitForState("Choose", NodeState.InputRequired);
        node.OpenConversationCommand.Execute(null);
        shell.WaitUntil(() => shell.Window.ViewModel.Conversation is { HasQuickReplies: true } || shell.Has<TextBox>("ConversationComposer"), "the conversation opens");
        shell.Render();
        return (shell, shell.Window.ViewModel.Conversation!);
    }

    [AvaloniaFact]
    public void A_click_fills_the_composer_and_sends_nothing()
    {
        using var f = Asking("Which window should I use?\n\n- The longer one\n- Each provider's own");
        var (shell, model) = Waiting(f);
        var buttons = Shell.ById<Button>(shell.Window, "ConversationQuickReply").ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, button => Assert.True(button.IsEffectivelyVisible));

        shell.Click(buttons[0]);

        Assert.Equal("The longer one", model.Draft);
        Assert.Equal(NodeState.InputRequired, shell.NodeModel("Choose").State);
        Assert.True(shell.Find<TextBox>("ConversationComposer").IsFocused);

        shell.Click(buttons[1]);
        Assert.Equal("Each provider's own", model.Draft);
    }

    [AvaloniaFact]
    public void A_digit_in_the_empty_composer_fills_it_and_a_draft_keeps_its_words()
    {
        using var f = Asking("Which window should I use?\n\n1. The longer one\n2. Each provider's own");
        var (shell, model) = Waiting(f);
        var composer = shell.Find<TextBox>("ConversationComposer");
        composer.Focus();

        shell.Press(Key.D2);
        Assert.Equal("Each provider's own", composer.Text);
        Assert.Equal(composer.Text!.Length, composer.CaretIndex);
        Assert.False(model.PickChoice(1));

        model.Draft = "Ask finance first.";
        model.FillReply(model.QuickReplies[0]);
        Assert.Equal("Ask finance first.\nThe longer one", model.Draft);
    }

    // A card holds the last marker, so the last row has no height, and the list estimates that row once it leaves the view.
    [AvaloniaFact]
    public void A_waiting_conversation_rests_at_its_end_at_every_height()
    {
        var words = string.Join(" ", Enumerable.Repeat("The sandbox and the provider disagree on the retry timing of the idempotency keys.", 24));
        using var f = Asking($"{words}\n\nWhich window should I use?\n\n- The longer one\n- Each provider's own");
        var (shell, _) = Waiting(f);
        var scroller = shell.Find<ScrollViewer>("TranscriptScroller");

        for (var height = 500; height < 760; height++)
        {
            shell.Window.Height = height;
            shell.Render();

            var end = scroller.Extent.Height - scroller.Viewport.Height;
            Assert.True(end > 0, $"At height {height} the transcript has nothing to scroll.");
            Assert.InRange(scroller.Offset.Y, end - 40, end + 0.5);
        }
    }

    [AvaloniaFact]
    public void A_question_without_a_list_shows_no_buttons_and_a_digit_types()
    {
        using var f = Asking("Should the window follow the longer one?");
        var (shell, model) = Waiting(f);

        Assert.False(model.HasQuickReplies);
        Assert.DoesNotContain(Shell.ById<Button>(shell.Window, "ConversationQuickReply"), button => button.IsEffectivelyVisible);
        Assert.False(model.PickChoice(1));
    }
}

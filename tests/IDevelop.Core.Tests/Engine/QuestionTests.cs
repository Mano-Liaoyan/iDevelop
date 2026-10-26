using IDevelop.Clients;
using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A question Claude Code asks on its own channel puts the node in input-required while its turn stays alive, and the
/// person's answer goes back into that turn.
/// </summary>
public sealed class QuestionTests : IAsyncLifetime
{
    private static readonly ExecutionSettings Claude = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" };

    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    /// <summary>A turn that asks which color, records the answer frame, and ends with <paramref name="reply"/>.</summary>
    private FakeRule Asks(string reply) => On().Print(FakeAgents.SessionLine(ClientId.ClaudeCode, "s-a"))
        .Print(FakeAgents.ClaudeAsks("Which color?", "Red", "Blue")).ReadLine(_f.File("answer.json"))
        .Print(FakeAgents.ReplyLines(ClientId.ClaudeCode, reply));

    private static QuestionsReply Choose(string option) => new([new QuestionAnswer("q:0", [option], null)]);

    [Fact]
    public async Task A_question_waits_for_the_person_in_its_running_turn_and_the_chosen_answer_resumes_it()
    {
        var a = _f.Node("A", ConversationMode.MayAsk, Claude);
        FakeAgents.Install(_f.Fakes, ClientId.ClaudeCode, Asks("Painted it blue."));
        var engine = await _f.Open();
        await engine.Run(a);

        var waiting = (NodeState.InputRequired)await Until(engine, a, state => state is NodeState.InputRequired { Question: not null });

        var asked = Assert.Single(waiting.Question!.Questions);
        Assert.Equal("Which color?", waiting.Why);
        Assert.Equal(["Red", "Blue"], asked.Options.Select(option => option.Label));
        Assert.Equal("It is running.", engine.CanRun(a));
        await engine.Answer(a, waiting.Question.Key, Choose("o:1"));

        await Until<NodeState.Completed>(engine, a);
        Assert.Contains("\"Which color?\":\"Blue\"", await File.ReadAllTextAsync(_f.File("answer.json")));
        Assert.Equal("Painted it blue.", Assert.Single(engine.History(a)).Report);
        Assert.Equal(1, engine.Entries(a).Count(entry => entry is NodeEntry.TurnStarted));
        var lines = engine.Conversation(a).Lines;
        Assert.Contains(new ConversationLine.Agent("Which color?"), lines);
        Assert.Contains(new ConversationLine.Person("Blue"), lines);
    }

    [Fact]
    public async Task An_answer_that_names_no_choice_of_the_question_is_refused_and_the_question_stays()
    {
        var a = _f.Node("A", ConversationMode.MayAsk, Claude);
        FakeAgents.Install(_f.Fakes, ClientId.ClaudeCode, Asks("Done."));
        var engine = await _f.Open();
        await engine.Run(a);
        var waiting = (NodeState.InputRequired)await Until(engine, a, state => state is NodeState.InputRequired { Question: not null });

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Answer(a, waiting.Question!.Key, Choose("o:7")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Answer(a, "another key", Choose("o:0")));

        Assert.Equal(waiting, engine.State(a));
        await engine.Answer(a, waiting.Question!.Key, new QuestionsReply([new QuestionAnswer("q:0", [], "Green")]));
        await Until<NodeState.Completed>(engine, a);
        Assert.Contains("\"Which color?\":\"Green\"", await File.ReadAllTextAsync(_f.File("answer.json")));
    }

    [Fact]
    public async Task A_stopped_turn_takes_no_answer_and_no_longer_shows_its_question()
    {
        var a = _f.Node("A", ConversationMode.MayAsk, Claude);
        FakeAgents.Install(_f.Fakes, ClientId.ClaudeCode, Asks("Done."));
        var engine = await _f.Open();
        await engine.Run(a);
        var waiting = (NodeState.InputRequired)await Until(engine, a, state => state is NodeState.InputRequired { Question: not null });

        var stopped = engine.Stop(a);

        Assert.IsNotType<NodeState.InputRequired>(engine.State(a));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Answer(a, waiting.Question!.Key, Choose("o:0")));
        await stopped.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(engine.Entries(a).OfType<NodeEntry.Answered>());
    }

    [Fact]
    public async Task A_question_still_waiting_when_iDevelop_closes_reopens_as_an_interrupted_turn()
    {
        var a = _f.Node("A", ConversationMode.MayAsk, Claude);
        FakeAgents.Install(_f.Fakes, ClientId.ClaudeCode, Asks("Done."));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until(engine, a, state => state is NodeState.InputRequired { Question: not null });

        await engine.DisposeAsync();
        var reopened = await _f.Open();

        Assert.Equal(new NodeState.InputRequired(NodeState.Interrupted), reopened.State(a));
        Assert.Single(reopened.Entries(a).OfType<NodeEntry.Asked>());
    }

    [Fact]
    public async Task An_Autonomous_node_s_client_question_is_declined_and_its_turn_goes_on()
    {
        var a = _f.Node("A", ConversationMode.Autonomous, Claude);
        FakeAgents.Install(_f.Fakes, ClientId.ClaudeCode, Asks("Chose myself."));
        var engine = await _f.Open();
        await engine.Run(a);

        await Until<NodeState.Completed>(engine, a);

        Assert.Contains("\"behavior\":\"deny\"", await File.ReadAllTextAsync(_f.File("answer.json")));
        Assert.Empty(engine.Entries(a).OfType<NodeEntry.Asked>());
    }
}

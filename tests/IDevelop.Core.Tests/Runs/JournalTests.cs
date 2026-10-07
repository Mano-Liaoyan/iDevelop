using IDevelop.Execution;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class JournalTests
{
    private const string Approved =
        "{\"schema\":1,\"sequence\":1,\"operation\":\"00000000-0000-0000-0000-000000000090\"," +
        "\"command\":{\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}," +
        "\"at\":\"2026-10-07T00:00:00Z\",\"event\":{\"type\":\"approved\",\"run\":\"00000000-0000-0000-0000-000000000010\"," +
        "\"revision\":{\"id\":{\"sha256\":\"f0be6cefd57fa43e04b8d205ec61a7b32d2277710dd002cc210aed7b0e6473bb\"}," +
        "\"snapshot\":{\"format\":\"idevelop.workflow/3\",\"id\":\"00000000-0000-0000-0000-000000000001\"," +
        "\"blueprints\":[],\"tasks\":[],\"connections\":[],\"layout\":{}}}," +
        "\"base\":{\"commit\":{\"hex\":\"1111111111111111111111111111111111111111\"},\"choice\":\"head\"}}}";

    [Fact]
    public void Literal_unknown_event_sequence_gap_and_torn_tail_return_typed_rejections()
    {
        var unknown = RunJournal.Decode("""{"schema":1,"sequence":1,"event":{"type":"future"}}""" + "\n");
        var gap = RunJournal.Decode("""{"schema":1,"sequence":2,"event":{"type":"stopRequested"}}""" + "\n");
        var torn = RunJournal.Decode(Approved + "\n" + """{"schema":1,"sequence":2,"event":""");
        Assert.Equal(new RunRejection(RunProblem.UnsupportedEvent, 1), unknown.Rejection);
        Assert.Equal(new RunRejection(RunProblem.SequenceGap, 1), gap.Rejection);
        Assert.Equal(new RunRejection(RunProblem.IncompleteTail, 2), torn.Rejection);
        var prefix = Assert.IsType<RunRead.Loaded>(RunReducer.Replay(W, Run, torn.Entries)).Record;
        Assert.Equal((Run, RunPhase.Approved, 1L), (prefix.Id, prefix.Phase, prefix.Sequence));
        Assert.Equal("f0be6cefd57fa43e04b8d205ec61a7b32d2277710dd002cc210aed7b0e6473bb", prefix.Revision.Id.Sha256);
    }

    [Theory]
    [InlineData("schema", RunProblem.UnsupportedSchema)]
    [InlineData("id", RunProblem.InvalidData)]
    [InlineData("hash", RunProblem.InvalidData)]
    [InlineData("enum", RunProblem.InvalidData)]
    [InlineData("complete", RunProblem.InvalidData)]
    public void Storage_validates_schema_ids_hashes_enums_and_complete_lines(string damage, object expected)
    {
        var json = damage switch
        {
            "schema" => Approved.Replace("\"schema\":1", "\"schema\":4"),
            "id" => Approved.Replace("00000000-0000-0000-0000-000000000010", "00000000-0000-0000-0000-000000000000"),
            "hash" => Approved.Replace("f0be6cefd57fa43e04b8d205ec61a7b32d2277710dd002cc210aed7b0e6473bb",
                "F0BE6CEFD57FA43E04B8D205EC61A7B32D2277710DD002CC210AED7B0E6473BB"),
            "enum" => Approved.Replace("\"head\"", "\"unknown\""),
            _ => "{invalid}",
        };
        Assert.Equal(new RunRejection((RunProblem)expected, 1), RunJournal.Decode(json + "\n").Rejection);
    }

    [Theory]
    [InlineData("unknown", RunProblem.UnsupportedEvent)]
    [InlineData("gap", RunProblem.SequenceGap)]
    [InlineData("torn", RunProblem.IncompleteTail)]
    public void Invalid_journals_block_mutation_and_preserve_original_bytes(string damage, object expected)
    {
        using var f = new RunFixtures();
        var folder = Path.Combine(f.Project, ".idp", "runs", W.ToString(), Run.ToString());
        Directory.CreateDirectory(folder);
        var original = Approved + "\n" + (damage switch
        {
            "unknown" => """{"schema":1,"sequence":2,"event":{"type":"future"}}""" + "\n",
            "gap" => """{"schema":1,"sequence":3,"event":{"type":"stopRequested"}}""" + "\n",
            _ => """{"schema":1,"sequence":2""",
        });
        var path = Path.Combine(folder, "events.jsonl");
        File.WriteAllText(path, original);
        var read = Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run));
        Assert.Equal(new RunRejection((RunProblem)expected, 2), read.Reason);
        Assert.Equal(RunPhase.Approved, read.Prefix!.Phase);
        Assert.Equal((RunProblem)expected, Problem(f.Store.Stop(new LegacyRun(W, Run), f.Op())));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Reducer_rejects_an_unknown_attempt_reference_at_its_sequence()
    {
        var approved = RunJournal.Decode(Approved + "\n").Entries;
        var closed = new RunEntry(1, 2, new(Id(91)), Prompt, At, new RunEvent.AttemptClosed(A1,
            new AttemptEnd.Recovered(RecoveryOutcome.Stopped, new(Id(92)), "Stopped.")));
        Assert.Equal(new RunRejection(RunProblem.UnknownAttempt, 2), Assert.IsType<RunRead.Rejected>(RunReducer.Replay(W, Run, [.. approved,
            closed])).Reason);
    }
}

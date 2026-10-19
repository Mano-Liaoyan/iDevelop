using System.Text;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// A journal of an earlier format stays on disk, and the app reads nothing from it, writes nothing to it, and lets it block no
/// workflow (#138, #145).
/// </summary>
public sealed class EarlierJournalTests
{
    /// <summary>The start of a run journal an earlier build wrote: an approval at a snapshot base, then a disposed capture.</summary>
    internal static string Earlier(RunId run) =>
        "{\"schema\":3,\"sequence\":1,\"operation\":\"00000000-0000-0000-0000-0000000e0090\"," +
        "\"command\":{\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}," +
        "\"at\":\"2026-10-07T00:00:00Z\",\"event\":{\"type\":\"approved\",\"run\":\"" + run + "\"," +
        "\"base\":{\"commit\":{\"hex\":\"1111111111111111111111111111111111111111\"},\"choice\":\"snapshot\"}}}\n" +
        "{\"schema\":3,\"sequence\":2,\"operation\":\"00000000-0000-0000-0000-0000000e0091\"," +
        "\"command\":{\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}," +
        "\"at\":\"2026-10-07T00:00:00Z\",\"event\":{\"type\":\"captureDisposed\"}}\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Only_a_first_line_of_an_earlier_schema_marks_an_earlier_journal(int schema)
    {
        Assert.True(RunJournal.Earlier(Encoding.UTF8.GetBytes(Earlier(OtherRun).Replace("\"schema\":3", $"\"schema\":{schema}"))));
        Assert.False(RunJournal.Earlier(Encoding.UTF8.GetBytes(Earlier(OtherRun).Replace("\"schema\":3", "\"schema\":4"))));
        Assert.False(RunJournal.Earlier(Encoding.UTF8.GetBytes(Earlier(OtherRun).Replace("\"schema\":3", "\"schema\":5"))));
        Assert.False(RunJournal.Earlier("{invalid}\n"u8));
        Assert.False(RunJournal.Earlier("{\"sequence\":1}\n"u8));
    }

    [Fact]
    public void An_earlier_journal_is_read_as_no_run_blocks_nothing_and_stays_as_it_is()
    {
        using var f = new RunFixtures();
        var earlier = Earlier(OtherRun);
        File.WriteAllText(f.Journal(W, OtherRun), earlier);

        Assert.Equal(new RunRejection(RunProblem.EarlierFormat), Assert.IsType<RunRead.Rejected>(f.Store.Read(W, OtherRun)).Reason);
        Assert.Empty(f.Store.Records(W));
        f.Approve();
        var reserved = f.Reserve();
        f.Claim(reserved);
        f.ReleaseControl();
        using (var owned = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, T)))
            Assert.Equal(new TaskRunOwnership.Owned("Workflow"), f.Store.TaskOwnership(owned));
        Assert.Equal([Run], f.Store.Records(W).Select(record => record.Id));
        Assert.Equal(RunProblem.EarlierFormat, Assert.IsType<ControlTake.Rejected>(f.Store.TakeControl(W, OtherRun)).Reason.Problem);
        Assert.Equal(RunProblem.EarlierFormat, Problem(f.Store.Approve(W, OtherRun, f.Op(), Revision.Capture(f.Workflow), new(Base))));
        Assert.Equal(earlier, File.ReadAllText(f.Journal(W, OtherRun)));
    }

    [Fact]
    public void A_new_journal_writes_the_current_schema_on_every_line()
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Claim(f.Reserve());
        var lines = File.ReadAllLines(f.Journal(W, Run));
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Equal(RunJournal.Schema, JsonDocument.Parse(line).RootElement.GetProperty("schema").GetInt32()));
        Assert.False(RunJournal.Earlier(File.ReadAllBytes(f.Journal(W, Run))));
    }
}

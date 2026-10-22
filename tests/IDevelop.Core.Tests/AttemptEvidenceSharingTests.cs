using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.AttemptEvents;

namespace IDevelop.Core.Tests;

/// <summary>An attempt's evidence reads while its log is still open for writing, as a run's launch reads it on Windows.</summary>
public sealed class AttemptEvidenceSharingTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Evidence_reads_a_log_its_writer_still_holds()
    {
        using var log = AttemptLog.Create(_temp.Create("attempts"), BuildRequested(First));
        log.Append(LaunchedAt1s);

        var evidence = AttemptEvidence.Read(log.Folder);

        Assert.Null(evidence.Rejection);
        Assert.Equal(2, evidence.Events.Length);
        Assert.Equal(evidence.Events[1..], AttemptEvidence.Suffix(log.Folder, Prefix(log.Folder), evidence.Checkpoint!));
    }

    private static LogCheckpoint Prefix(string folder)
    {
        using var stream = new FileStream(Path.Combine(folder, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var first = Array.IndexOf(bytes, (byte)'\n') + 1;
        return new(first, Revision.Hash(bytes[..first]));
    }
}

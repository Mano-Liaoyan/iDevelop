using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record AttemptEvidence(ImmutableArray<AttemptEvent> Events, AttemptRecord? Record, LogCheckpoint? Checkpoint, RunRejection? Rejection)
{
    private static readonly JsonSerializerOptions Options = new(AttemptLog.Options)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static AttemptEvidence Read(string folder, LogCheckpoint? expected = null)
    {
        AttemptEvidence Reject(RunProblem problem) => new([], null, null, new(problem));
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, "events.jsonl"));
            var checkpoint = new LogCheckpoint(bytes.LongLength, Revision.Hash(bytes));
            if (expected is not null && (!RunValidation.Checkpoint(expected) || expected != checkpoint))
            {
                return Reject(RunProblem.EvidenceMismatch);
            }

            if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
            {
                return Reject(RunProblem.EvidenceMismatch);
            }

            var text = new UTF8Encoding(false, true).GetString(bytes);
            var events = ImmutableArray.CreateBuilder<AttemptEvent>();
            AttemptRecord? record = null;
            foreach (var line in text.Split('\n')[..^1])
            {
                var next = JsonSerializer.Deserialize<AttemptEvent>(line, Options);
                if (next is null)
                {
                    return Reject(RunProblem.EvidenceMismatch);
                }

                if (record is null)
                {
                    if (next is not AttemptEvent.Requested request || request.Attempt.Value == Guid.Empty || request.Task.Value == Guid.Empty ||
                        request.RunBinding is { } binding && (binding.Workflow.Value == Guid.Empty || binding.Run.Value == Guid.Empty ||
                            binding.InitialInputs.Value == Guid.Empty || !Revision.IsHash(binding.Revision.Sha256)))
                    {
                        return Reject(RunProblem.EvidenceMismatch);
                    }

                    record = AttemptReducer.Start(request);
                }
                else
                {
                    if (next is AttemptEvent.Requested || next is AttemptEvent.TurnRequested && !record.BetweenTurns ||
                        next is AttemptEvent.Exited && record.BetweenTurns ||
                        record.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Interrupted &&
                            next is not AttemptEvent.HandedToTerminal)
                    {
                        return Reject(RunProblem.EvidenceMismatch);
                    }

                    record = AttemptReducer.Apply(record, next);
                }
                events.Add(next);
            }
            return new(events.ToImmutable(), record, checkpoint, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            DecoderFallbackException or ArgumentException or InvalidOperationException or ProjectException or BlueprintException or FormatException)
        {
            return Reject(RunProblem.EvidenceMismatch);
        }
    }

    public static TerminalAttemptOutcome? Terminal(AttemptEvidence read) => read.Record?.Status switch
    {
        AttemptStatus.Succeeded => TerminalAttemptOutcome.Succeeded,
        AttemptStatus.Failed => TerminalAttemptOutcome.Failed,
        AttemptStatus.Cancelled => TerminalAttemptOutcome.Cancelled,
        AttemptStatus.Interrupted => TerminalAttemptOutcome.Interrupted,
        _ => null,
    };

    public static bool Matches(AttemptEvidence read, TerminalAttemptOutcome outcome) => read.Rejection is null &&
        Terminal(read) == outcome && read.Record!.EndedAt is not null && read.Events.All(e => e is not AttemptEvent.Reconciled) &&
        read.Events.Any(e => e is AttemptEvent.Exited or AttemptEvent.LaunchFailed or AttemptEvent.MarkedDone or AttemptEvent.Concluded or
            AttemptEvent.CancelRequested);
}

internal sealed record ReusableReport(string? Report, ReuseEvidence? Evidence, RunRejection? Rejection);

internal enum TreeComparison { ContentMatch, ReuseUnverifiable }

internal static class ReportReuse
{
    public static ReusableReport Validate(string project, TaskDefinition task, AttemptSource.Standalone source, CommitId codeBase,
        OperationId confirmation)
    {
        ReusableReport Refuse() => new(null, null, new(RunProblem.ReuseUnverifiable));
        if (confirmation.Value == Guid.Empty)
        {
            return new(null, null, new(RunProblem.ConfirmationRequired));
        }

        if (task.Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.ReadOnly, Proposes: false } || task.Execution is null ||
            source.Task != task.Id || source.Attempt.Value == Guid.Empty || !Revision.IsCommit(codeBase.Hex))
        {
            return Refuse();
        }

        var read = AttemptEvidence.Read(AttemptLog.FolderOf(DataFolder.Attempts(project), source.Task, source.Attempt));
        if (read.Rejection is not null || read.Record is not
            {
                Status: AttemptStatus.Succeeded, Turns.Count: 1, Continues: null, Terminal: null,
                Planning: null, Subject: null, Fix: null, Result: not null
            } record || !AttemptEvidence.Matches(read, TerminalAttemptOutcome.Succeeded))
        {
            return Refuse();
        }

        var request = (AttemptEvent.Requested)read.Events[0];
        if (request.StandaloneCapture is not { Inputs: "" } capture || request.RunBinding is not null || !request.ReadOnly ||
            request.Planning is not null ||
            Revision.CanonicalTask(capture.Definition) != Revision.CanonicalTask(task) || request.Settings != task.Execution ||
            request.Conversation != task.Conversation || request.Prompt != Prompt(task, "") || request.TaskTitle != task.Title ||
            record.StartTree is not { } before || record.EndTree is not { } after ||
            read.Events.Any(e => e is AttemptEvent.TurnRequested or AttemptEvent.HandedToTerminal or AttemptEvent.MarkedDone or
                AttemptEvent.Concluded or AttemptEvent.MessageQueued))
        {
            return Refuse();
        }

        var comparison = CompareTrees(project, before, after, codeBase);
        if (comparison.Comparison != TreeComparison.ContentMatch)
        {
            return Refuse();
        }

        return new(record.Result, new(read.Checkpoint!, Revision.Hash(Revision.CanonicalTask(task)), Revision.Hash(""),
            comparison.Content!.Value, confirmation), null);
    }

    internal static string Prompt(TaskDefinition task, string inputs) => AgentWork.Prompt(new NodeContext(task, inputs));

    internal static (TreeComparison Comparison, Digest? Content) CompareTrees(string project, string before, string after, CommitId codeBase)
    {
        if (!Revision.IsCommit(before) || !Revision.IsCommit(after) || !Revision.IsCommit(codeBase.Hex))
        {
            return (TreeComparison.ReuseUnverifiable, null);
        }

        var start = GitTree.ContentOutsideData(project, before);
        var end = GitTree.ContentOutsideData(project, after);
        var receiving = GitTree.ContentOutsideData(project, codeBase.Hex);
        return start is not null && start == end && start == receiving
            ? (TreeComparison.ContentMatch, Revision.Hash(start)) : (TreeComparison.ReuseUnverifiable, null);
    }
}

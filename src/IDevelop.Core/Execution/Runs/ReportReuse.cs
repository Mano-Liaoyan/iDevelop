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

    public static AttemptEvidence Read(string folder, LogCheckpoint? expected = null) => ReadCore(folder, expected, false);

    public static AttemptEvidence ReadPrefix(string folder, LogCheckpoint expected) => ReadCore(folder, expected, true);

    private static AttemptEvidence ReadCore(string folder, LogCheckpoint? expected, bool prefix)
    {
        AttemptEvidence Reject(RunProblem problem) => new([], null, null, new(problem));
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, "events.jsonl"));
            if (prefix && expected is { } limit)
            {
                if (limit.ByteLength < 0 || limit.ByteLength > bytes.LongLength) return Reject(RunProblem.EvidenceMismatch);
                bytes = bytes[..checked((int)limit.ByteLength)];
            }
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

    /// <summary>
    /// The events a log adds after <paramref name="prefix"/> up to <paramref name="whole"/>, read once and parsed with the
    /// strict options. Null when the log's first bytes are not exactly <paramref name="whole"/>, or do not begin with
    /// <paramref name="prefix"/>.
    /// </summary>
    public static ImmutableArray<AttemptEvent>? Suffix(string folder, LogCheckpoint prefix, LogCheckpoint whole)
    {
        try
        {
            if (!RunValidation.Checkpoint(prefix) || !RunValidation.Checkpoint(whole) || prefix.ByteLength > whole.ByteLength) return null;
            var bytes = File.ReadAllBytes(Path.Combine(folder, "events.jsonl"));
            if (whole.ByteLength > bytes.LongLength) return null;
            bytes = bytes[..checked((int)whole.ByteLength)];
            if (Revision.Hash(bytes) != whole.Content || Revision.Hash(bytes.AsSpan(0, checked((int)prefix.ByteLength))) != prefix.Content) return null;
            var rest = bytes[checked((int)prefix.ByteLength)..];
            if (rest.Length == 0) return [];
            if (rest[^1] != (byte)'\n') return null;
            var events = ImmutableArray.CreateBuilder<AttemptEvent>();
            foreach (var line in new UTF8Encoding(false, true).GetString(rest).Split('\n')[..^1])
            {
                if (JsonSerializer.Deserialize<AttemptEvent>(line, Options) is not { } next) return null;
                events.Add(next);
            }
            return events.ToImmutable();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            DecoderFallbackException or ArgumentException or InvalidOperationException or ProjectException or BlueprintException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a closure log is the turn's checkpoint, or that checkpoint and one Mark done line.</summary>
    public static bool Extends(string folder, LogCheckpoint turn, LogCheckpoint closure) =>
        closure == turn || Suffix(folder, turn, closure) is [AttemptEvent.MarkedDone];

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

/// <summary>A planner's report that a run may include, with what it checked, or why it may not.</summary>
internal sealed record PlannerReport(string? Report, InclusionEvidence? Evidence, RunRejection? Rejection);

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
        StandaloneCapture? capture;
        try
        {
            capture = request.StandaloneCapture?.Deserialize<StandaloneCapture>(RunJournal.Options);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or
            ProjectException or BlueprintException or FormatException)
        {
            return Refuse();
        }

        if (request.Attempt != source.Attempt || request.Task != source.Task || capture is not { Inputs: "" } ||
            request.RunBinding is not null || !request.ReadOnly ||
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

    /// <summary>
    /// Checks a standalone planner's attempt for inclusion in a run approved as <paramref name="approved"/> on
    /// <paramref name="codeBase"/>: a fresh, read-only root planner of the approved definition and settings, whose final
    /// turn <paramref name="turn"/> ended with a readable proposal, which ended in strict success without a terminal
    /// handoff, a reconciliation, or unsent text, and whose every turn started and ended on the base's content. The approved
    /// workflow must already hold the part of the proposal the person accepted, so accepting that part again changes nothing.
    /// </summary>
    /// <param name="waiting">
    /// Also accepts an attempt that waits for the person after <paramref name="turn"/>, which a confirmation finishes
    /// through Mark done. Its evidence then describes the waiting log, so only a preview uses it.
    /// </param>
    public static PlannerReport ValidatePlanner(string project, Workflow approved, TaskId taskId, AttemptSource.Standalone source, int turn,
        CommitId codeBase, OperationId confirmation, bool waiting = false)
    {
        PlannerReport Refuse() => new(null, null, new(RunProblem.ReuseUnverifiable, Task: taskId));
        if (confirmation.Value == Guid.Empty) return new(null, null, new(RunProblem.ConfirmationRequired, Task: taskId));
        if (!approved.Tasks.TryGetValue(taskId, out var task) ||
            task.Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.ReadOnly, Proposes: true } ||
            approved.Connections.Keys.Any(edge => edge.To == task.Id))
        {
            return Refuse();
        }

        var read = AttemptEvidence.Read(AttemptLog.FolderOf(DataFolder.Attempts(project), source.Task, source.Attempt));
        if (read.Rejection is not null || read.Record is not { Continues: null, Terminal: null, ReadOnly: true } record ||
            record.Id != source.Attempt || record.Task != task.Id || record.Turns.Count != turn || !record.Queued.IsEmpty)
        {
            return Refuse();
        }

        // A waiting planner counts only for a preview, whose confirmation marks it done before the approval checks it again.
        if (!(record.Status == AttemptStatus.Succeeded && AttemptEvidence.Matches(read, TerminalAttemptOutcome.Succeeded) ||
              waiting && record is { Status: AttemptStatus.WaitingForInput, BetweenTurns: true }))
        {
            return Refuse();
        }

        var request = (AttemptEvent.Requested)read.Events[0];
        StandaloneCapture? capture;
        try
        {
            capture = request.StandaloneCapture?.Deserialize<StandaloneCapture>(RunJournal.Options);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or
            ProjectException or BlueprintException or FormatException)
        {
            return Refuse();
        }

        // The prompt goes on to list the slots and types as the workflow then had them, which the approved workflow no longer shows.
        if (capture is not { Inputs: "" } || request.RunBinding is not null ||
            Revision.CanonicalTask(capture.Definition) != Revision.CanonicalTask(task) || request.Settings != task.Execution ||
            !request.Prompt.StartsWith(AgentWork.Ticket(task) + "\n\n", StringComparison.Ordinal))
        {
            return Refuse();
        }

        var receiving = GitTree.ContentOutsideData(project, codeBase.Hex);
        if (receiving is null || record.Turns.Any(each => each.StartTree is not { } before || each.EndTree is not { } after ||
            GitTree.ContentOutsideData(project, before) != receiving || GitTree.ContentOutsideData(project, after) != receiving))
        {
            return Refuse();
        }

        if (Proposal.Read(record, key => approved.Blueprints.GetValueOrDefault(key) ?? BuiltInBlueprints.Find(key)) is not ProposalRead.Ready
            { Proposal: var proposal } || proposal.Turn != turn || Accepted(approved, proposal) is not { } accepted)
        {
            return Refuse();
        }

        var selection = Revision.Hash(RunJournal.Canonical(new { proposal, accepted = accepted.Order().ToArray() }));
        return new(record.Turns[^1].FinalText, new(read.Checkpoint!, turn, Revision.Hash(Revision.CanonicalTask(task)), selection,
            Revision.Hash(receiving), confirmation), null);
    }

    /// <summary>
    /// The items of <paramref name="proposal"/> that <paramref name="workflow"/> holds: each node it added, and each fill
    /// whose title and fields are in place. Null when accepting them again would still change the workflow, as when a
    /// connection among them is missing.
    /// </summary>
    internal static ImmutableHashSet<TaskId>? Accepted(Workflow workflow, Proposal proposal)
    {
        var accepted = proposal.Nodes.Where(node => workflow.Tasks.ContainsKey(node.Id)).Select(node => node.Id)
            .Concat(proposal.Fills.Where(fill => workflow.Tasks.TryGetValue(fill.Slot, out var slot) &&
                (fill.Title is null || slot.Title == fill.Title) && fill.Fields.All(field => slot.Field(field.Key) == field.Value))
                .Select(fill => fill.Slot))
            .ToImmutableHashSet();
        return workflow.Apply(proposal.Accept(workflow, accepted, _ => false)) is EditResult.Applied applied &&
            Revision.Canonical(applied.Workflow) == Revision.Canonical(workflow) ? accepted : null;
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

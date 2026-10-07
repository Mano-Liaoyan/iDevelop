using System.Collections.Immutable;
using System.Diagnostics;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>
    /// Drives every review that rests between its reviewer's turns: it asks the review's work for the next step and takes
    /// it. One caller at a time. Each step checks under the gate that the attempts it read are still the latest, so a step
    /// taken twice, or after another window moved on, does nothing. A step that settles at once lets the next pass go on.
    /// A step that starts a run goes on when that run ends.
    /// </summary>
    private void Advance()
    {
        lock (_advancing)
        {
            for (var pass = 0; pass < 16 && AdvanceOnce(); pass++)
            {
            }
        }
    }

    /// <returns>True when a step changed an attempt without starting a run, so another pass may take the next step.</returns>
    private bool AdvanceOnce()
    {
        ImmutableDictionary<WorkflowId, Workflow> workflows;
        ImmutableDictionary<TaskId, AttemptRecord> latest;
        lock (_gate)
        {
            if (_leaving is not null || _workflows.IsEmpty)
            {
                return false;
            }

            (workflows, latest) = (_workflows, Latest);
        }

        var progressed = false;
        foreach (var review in latest.Values.Where(record => record.Status == AttemptStatus.InReview).ToList())
        {
            var workflow = workflows.Values.FirstOrDefault(workflow => workflow.Tasks.ContainsKey(review.Task));
            if (workflow?.Tasks.GetValueOrDefault(review.Task) is not { Blueprint.Work: WorkSpec.Review } node)
            {
                continue;
            }

            var subject = Subject(node.Id, workflow, latest, readChanges: true, recorded: review.Subject);
            progressed |= ReviewWork.Instance.Next(new NodeContext(node, "") { Subject = subject }, review) switch
            {
                NodeStep.WaitForSubject => Unstall(node.Id),
                NodeStep.Finish => Conclude(review, failure: null),
                NodeStep.Fail fail => Conclude(review, fail.Reason),
                NodeStep.RunTurn turn => ReviewTurn(node, review, turn),
                NodeStep.FixRound fix => StartFix(review, subject!, fix),
                var step => throw new UnreachableException($"A review does not take {step.GetType().Name}."),
            };
        }

        return progressed;
    }

    /// <summary>The read attempt is still the task's latest, in the same state. Called under the gate.</summary>
    private bool Unchanged(AttemptRecord read) =>
        Latest.GetValueOrDefault(read.Task) is { } now && now.Id == read.Id && now.Status == read.Status &&
        now.Turns.Count == read.Turns.Count && now.Guidance.Count == read.Guidance.Count;

    private bool Conclude(AttemptRecord review, string? failure)
    {
        Append(review.Task, current => current.Status == AttemptStatus.InReview && current.Id == review.Id && current.Turns.Count == review.Turns.Count,
            new AttemptEvent.Concluded(TimeProvider.GetUtcNow(), failure), out var appended);
        if (appended)
        {
            Unstall(review.Task);
            NotifyChanged();
        }

        return appended;
    }

    /// <summary>The reviewer's next turn, which resumes the review attempt's session with the settings it started with.</summary>
    private bool ReviewTurn(TaskDefinition node, AttemptRecord review, NodeStep.RunTurn turn)
    {
        ActiveRun? run = null;
        var tree = GitTree.Snapshot(_projectFolder);
        lock (_gate)
        {
            if (_leaving is not null || _active.ContainsKey(node.Id))
            {
                return false;
            }

            switch (TakeLock(node.Id))
            {
                case LockTake.Taken taken when !Unchanged(review):
                    taken.Lock.Dispose();
                    return true;
                case LockTake.Taken taken:
                    var (result, started) = Answer(node with { Execution = review.Requested }, turn.Prompt, Latest[node.Id], taken.Lock, tree, turn.Report);
                    if (result is SendResult.Refused refused)
                    {
                        return Stall(node.Id, refused.Problem is SendProblem.CannotStart cannot ? cannot.Problem : new StartProblem.InReview(node.Title));
                    }

                    _stalls = _stalls.Remove(node.Id);
                    run = started;
                    break;
                case LockTake.HeldElsewhere or LockTake.Failed:
                    return false;
            }
        }

        Announce(run);
        return run is null;
    }

    /// <summary>
    /// A new attempt of the subject for the fix round, which resumes the subject's latest session, or starts a fresh one
    /// when that session cannot go on. A start that the subject's client refuses leaves the review resting and says why.
    /// </summary>
    private bool StartFix(AttemptRecord review, SubjectView subject, NodeStep.FixRound fix)
    {
        var node = subject.Node;
        ActiveRun? run = null;
        var tree = GitTree.Snapshot(_projectFolder);
        lock (_gate)
        {
            if (_leaving is not null || _active.ContainsKey(node.Id))
            {
                return false;
            }

            switch (TakeLock(node.Id))
            {
                case LockTake.Taken taken when !Unchanged(review) || Latest.GetValueOrDefault(node.Id)?.Id != subject.Latest?.Id:
                    taken.Lock.Dispose();
                    return true;
                case LockTake.Taken taken:
                    Continuation? from = null;
                    if (fix.Resumes && !TryContinue(node, Latest.GetValueOrDefault(node.Id), out from, out _))
                    {
                        taken.Lock.Dispose();
                        return true;
                    }

                    var verdict = StartCheck.Evaluate(node, _projectFolder, _clients.Current, new Resumption(from?.Session, fix.Prompt), questions: _questions);
                    if (verdict is StartVerdict.Blocked blocked)
                    {
                        taken.Lock.Dispose();
                        return Stall(review.Task, new StartProblem.SubjectBlocked(node.Title, blocked.Problem));
                    }

                    var link = new ReviewLink(review.Task, review.Id, fix.Round, fix.Guidance);
                    var (result, started) = RecordAndLaunch(node, ((StartVerdict.Allowed)verdict).Plan, taken.Lock, from, tree, fix: link);
                    if (result is StartResult.Refused refused)
                    {
                        return Stall(review.Task, new StartProblem.SubjectBlocked(node.Title, refused.Problem));
                    }

                    _stalls = _stalls.Remove(review.Task);
                    run = started;
                    break;
                case LockTake.HeldElsewhere or LockTake.Failed:
                    return false;
            }
        }

        Announce(run);
        return run is null;
    }

    /// <summary>Records why the review cannot go on, and says so once. Called under the gate. Returns false: nothing moved.</summary>
    private bool Stall(TaskId review, StartProblem problem)
    {
        if (_stalls.GetValueOrDefault(review) != problem)
        {
            _stalls = _stalls.SetItem(review, problem);
            ThreadPool.QueueUserWorkItem(_ => NotifyChanged());
        }

        return false;
    }

    private bool Unstall(TaskId review)
    {
        lock (_gate)
        {
            _stalls = _stalls.Remove(review);
        }

        return false;
    }

    /// <summary>
    /// The person's guidance to a review, while its reviewer's turn runs or while it rests between turns. A turn that starts
    /// or ends between the two checks sends it to the other place.
    /// </summary>
    private async Task<SendResult> GuideAsync(TaskDefinition task, string text, CancellationToken ct, TurnKey? expected)
    {
        for (var tries = 0; ; tries++)
        {
            ActiveRun? active;
            lock (_gate)
            {
                if (SendTarget(task, expected) is { } target)
                {
                    return new SendResult.Refused(target);
                }

                task = Resolve(task.Id) ?? task;
                _active.TryGetValue(task.Id, out active);
            }

            if (active is not null)
            {
                var result = await active.GuideAsync(task, text, ct, expected);
                if (result is not SendResult.Refused { Problem: SendProblem.Ending })
                {
                    return result;
                }
            }

            StartProblem? problem;
            bool appended;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (SendTarget(task, expected) is { } target)
                {
                    return new SendResult.Refused(target);
                }

                problem = Append(task.Id, current => (expected is null || Current(task.Id) == expected)
                    && current.Status == AttemptStatus.InReview, new AttemptEvent.GuidanceAdded(TimeProvider.GetUtcNow(), text), out appended);
                if (expected is not null && Current(task.Id) != expected)
                {
                    return new SendResult.Refused(new SendProblem.StaleTarget());
                }
            }

            if (problem is not null)
            {
                return new SendResult.Refused(new SendProblem.CannotStart(problem));
            }

            if (appended)
            {
                NotifyChanged();
                return new SendResult.Guided();
            }

            if (tries == 2 || Latest.GetValueOrDefault(task.Id) is not { Status: AttemptStatus.Running or AttemptStatus.InReview })
            {
                return new SendResult.Refused(new SendProblem.NotReviewing(task.Title));
            }

            await Task.Delay(50, ct);
        }
    }
}

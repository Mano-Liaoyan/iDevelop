using IDevelop.Workflows;

namespace IDevelop.Execution;

// THROWAWAY N1 PROTOTYPE. Never merge. Lets an expanded card read a run-owned task's attempt, as its conversation does.
public sealed partial class ProjectRuns
{
    internal AttemptRecord? RunAttempt(RunAddress address, TaskId task, AttemptId attempt) =>
        ReadRunRecord(address.Workflow, address.Run, task, attempt).Record;
}

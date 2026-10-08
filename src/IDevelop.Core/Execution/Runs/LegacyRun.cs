using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record LegacyRun(WorkflowId Workflow, RunId Run);

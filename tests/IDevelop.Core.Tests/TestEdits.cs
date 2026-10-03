using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

internal static class TestEdits
{
    public static Workflow Must(this Workflow workflow, WorkflowEdit edit) =>
        Assert.IsType<EditResult.Applied>(workflow.Apply(edit)).Workflow;

    public static EditRejection Rejection(this Workflow workflow, WorkflowEdit edit) =>
        Assert.IsType<EditResult.Rejected>(workflow.Apply(edit)).Reason;
}

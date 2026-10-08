using IDevelop.Execution;

namespace IDevelop.Core.Tests.Git;

public sealed class RunLayoutTests
{
    [Fact]
    public void Keys_hash_the_full_identity_and_lengthen_only_on_collision()
    {
        var first = Guid.Parse("019a9d2e-0000-7000-8000-000000000001");
        var second = Guid.Parse("019a9d2e-0000-7000-8000-000000000002");
        Assert.Equal("3940f0a5", RunLayout.Key(first, new HashSet<string>()));
        Assert.Equal("b258126a", RunLayout.Key(second, new HashSet<string>()));
        Assert.Equal("3940f0a5d", RunLayout.Key(first, new HashSet<string> { "3940f0a5" }));
        Assert.Equal("3940f0a5df", RunLayout.Key(first, new HashSet<string> { "3940f0a5", "3940f0a5d" }));
    }

    [Fact]
    public void Names_separate_task_join_result_and_salvage_namespaces_with_full_ids()
    {
        var attempt = new AttemptId(Guid.Parse("019a9d2e-0000-7000-8000-000000000001"));
        var operation = new OperationId(Guid.Parse("019a9d2e-0000-7000-8000-000000000002"));
        var input = new InputId(operation.Value);
        Assert.Equal(".worktrees/3940f0a5/b258126a", RunLayout.TaskCheckout("3940f0a5", "b258126a"));
        Assert.Equal("refs/heads/idp/3940f0a5/task/b258126a", RunLayout.TaskBranch("3940f0a5", "b258126a"));
        Assert.Equal("idp/3940f0a5/task/b258126a", RunLayout.TaskBranchShortName("3940f0a5", "b258126a"));
        Assert.Equal("refs/heads/idp/3940f0a5/join/b258126a", RunLayout.JoinBranch("3940f0a5", "b258126a"));
        Assert.Equal("refs/idp/3940f0a5/result/b258126a/019a9d2e-0000-7000-8000-000000000001", RunLayout.ResultRef("3940f0a5", "b258126a", attempt));
        Assert.Equal("refs/idp/3940f0a5/salvage/b258126a/019a9d2e-0000-7000-8000-000000000001", RunLayout.SalvageRef("3940f0a5", "b258126a", attempt));
        Assert.Equal("refs/idp/3940f0a5/resalvage/b258126a/019a9d2e-0000-7000-8000-000000000001/019a9d2e-0000-7000-8000-000000000002",
            RunLayout.ResalvageRef("3940f0a5", "b258126a", attempt, operation));
        Assert.Equal("refs/idp/3940f0a5/base", RunLayout.ApprovedBase("3940f0a5"));
        Assert.Equal(".idp/outbox/019a9d2e-0000-7000-8000-000000000001", RunLayout.Outbox(attempt));
        Assert.Equal(".idp/inputs/019a9d2e-0000-7000-8000-000000000002", RunLayout.InputFolder(input));
    }

    [Fact]
    public void Capture_and_preservation_pins_use_full_attempt_and_operation_ids()
    {
        var attempt = new AttemptId(Guid.Parse("019a9d2e-0000-7000-8000-000000000001"));
        var operation = new OperationId(Guid.Parse("019a9d2e-0000-7000-8000-000000000002"));
        Assert.Equal("refs/idp/3940f0a5/pin/b258126a/019a9d2e-0000-7000-8000-000000000001/3/capture-2-index",
            RunLayout.CaptureIndexPin("3940f0a5", "b258126a", new(attempt, 3), 2));
        Assert.Equal("refs/idp/3940f0a5/pin/b258126a/019a9d2e-0000-7000-8000-000000000001/preserve/019a9d2e-0000-7000-8000-000000000002/2",
            RunLayout.PreservationPin("3940f0a5", "b258126a", attempt, operation, 2));
        Assert.Equal("refs/idp/3940f0a5/preserve/b258126a/019a9d2e-0000-7000-8000-000000000002",
            RunLayout.PreserveRef("3940f0a5", "b258126a", operation));
    }

    [Fact]
    public void Used_run_keys_include_branches_and_retention_refs_but_not_neighbors()
    {
        var refs = new Dictionary<string, CommitId>
        {
            ["refs/heads/idp/3940f0a5/task/b258126a"] = new("adfe40b30c176fb407933286f51d15ea9b54cdc3"),
            ["refs/idp/b258126a/base"] = new("adfe40b30c176fb407933286f51d15ea9b54cdc3"),
            ["refs/idp/3940f0a5/result/b258126a/attempt"] = new("adfe40b30c176fb407933286f51d15ea9b54cdc3"),
            ["refs/heads/idp/blocked"] = default,
            ["refs/idp/bare"] = default,
            ["refs/heads/other/run/task/t"] = default,
            ["refs/heads/idp-neighbor/run/task/t"] = default,
        };
        Assert.Equal(["3940f0a5", "b258126a", "bare", "blocked"], RunLayout.UsedRunKeys(refs).Order(StringComparer.Ordinal));
    }
}

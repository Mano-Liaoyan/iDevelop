using IDevelop.Execution;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Approval;

/// <summary>A repeated confirmation, or another confirmation of the same content, approves one run (E3d.1).</summary>
public sealed class RepeatedConfirmationTests
{
    private static OperationId Command(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000d{n:D3}"));

    [Fact]
    public async Task Repeating_a_confirmation_or_confirming_the_same_content_again_starts_one_run()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Preflight();

        var starts = await Task.WhenAll(Start(f, preview, BaseChoice.Head, Command(1)), Start(f, preview, BaseChoice.Head, Command(1)),
            Start(f, f.Preflight(), BaseChoice.Head, Command(2)));

        var run = Assert.Single(starts.Select(start => start.Coordinator.Address.Run).Distinct());
        Assert.Equal(1, starts.Count(start => !start.Existing));
        Assert.Same(starts[0].Coordinator, starts[2].Coordinator);
        await Completed(starts[0].Coordinator);
        Assert.Equal([run], f.ApprovedRuns());
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });

        foreach (var command in new[] { Command(1), Command(2) })
        {
            var again = await Start(f, preview, BaseChoice.Head, command);
            Assert.True(again.Existing);
            Assert.Equal(run, again.Coordinator.Address.Run);
            Assert.Equal(RunProblem.RunStopped, Assert.IsType<RunCommand.Refused>(again.Resume).Reason.Problem);
        }
        Assert.Equal([run], f.ApprovedRuns());
        Assert.Equal(3, f.TotalLaunches);
    }

    [Fact]
    public async Task After_a_run_settles_a_new_confirmation_of_the_same_content_starts_another_run()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        f.Answer(A, Writes(A, "out-a.txt", "A\n"), Writes(A, "out-a.txt", "A\n"))
            .Answer(B, Writes(B, "out-b.txt", "B\n"), Writes(B, "out-b.txt", "B\n")).Answer(X, Reports(X), Reports(X));
        await f.Open();
        var first = await Start(f, f.Preflight(), BaseChoice.Head, Command(3));
        await Completed(first.Coordinator);

        var again = f.Preflight();
        Assert.Equal((0, 0), (again.Base!.Changed.Length, again.Base.Ignored.Length));
        Assert.Equal<BaseChoice>([BaseChoice.Head], again.Choices);
        var second = await Start(f, again, BaseChoice.Head, Command(4));

        Assert.False(second.Existing);
        Assert.NotEqual(first.Coordinator.Address.Run, second.Coordinator.Address.Run);
        await Completed(second.Coordinator);
        Assert.Equal(2, f.ApprovedRuns().Length);
        Assert.Equal([2, 2, 2], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
    }

    [Fact]
    public async Task Two_windows_confirming_the_same_content_at_once_start_one_run_that_one_window_controls()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var second = await f.Window();

        var starts = await Task.WhenAll(Start(f, f.Preflight(), BaseChoice.Head, Command(5)),
            Start(f, second.Preflight(f.Workflow), BaseChoice.Head, Command(6), second));

        var run = Assert.Single(starts.Select(start => start.Coordinator.Address.Run).Distinct());
        var controlled = Assert.Single(starts, start => start.Coordinator.Controlled);
        var reader = Assert.Single(starts, start => !start.Coordinator.Controlled);
        Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<RunCommand.Unavailable>(reader.Resume).Message);
        await Completed(controlled.Coordinator);
        Assert.Equal([run], f.ApprovedRuns());
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
    }
}

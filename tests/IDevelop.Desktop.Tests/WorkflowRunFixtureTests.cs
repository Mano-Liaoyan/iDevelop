using Avalonia.Headless.XUnit;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>The scratch project that <see cref="WorkflowRunFixture"/> runs a workflow in.</summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkflowRunFixtureTests
{
    // A settled run releases its retention pins with Git after its bar says Completed. A teardown that deleted the project
    // under that Git failed with "Directory not empty" under load (#86).
    [AvaloniaFact]
    public void Disposing_lets_the_runs_work_in_flight_end_before_it_deletes_the_project()
    {
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var f = new WorkflowRunFixture(Task(TestTasks.Design, "A", 105)))
        {
            f.Answer("A", f.Writes("result.txt", "done\n", "A ready."));
            var git = Path.Combine(f.Project, ".git");
            f.Probe = point =>
            {
                if (point != "git.release-pin.before") return;
                // A teardown that does not wait for the release gets this second to delete the project first.
                SpinWait.SpinUntil(() => !Directory.Exists(git), TimeSpan.FromSeconds(1));
                released.TrySetResult(Directory.Exists(git));
            };
            var shell = f.Window();
            shell.StartRun();
            shell.WaitForStatus("Completed");
        }

        Assert.True(released.Task.Wait(TimeSpan.FromSeconds(10)), "The run released no pin.");
        Assert.True(released.Task.Result, "The project was deleted while the run released its pins.");
    }
}

using IDevelop.Engine;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests.Engine;

/// <summary>One window per project.</summary>
public sealed class ProjectLockTests
{
    [Fact]
    public void A_second_window_cannot_take_a_project_that_another_window_holds()
    {
        using var temp = new TempFolder();
        var project = temp.Create("project");

        var first = ProjectLock.TryTake(project);
        var second = ProjectLock.TryTake(project);
        first?.Dispose();
        using var third = ProjectLock.TryTake(project);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.NotNull(third);
    }

    [Fact]
    public void A_lock_that_cannot_keep_its_folder_out_of_Git_lets_the_project_go()
    {
        using var temp = new TempFolder();
        var project = temp.Create("project");
        // A folder where the data folder's .gitignore goes keeps it from being written.
        var ignore = Directory.CreateDirectory(Path.Combine(project, ".idp", ".gitignore")).FullName;

        Assert.ThrowsAny<Exception>(() => ProjectLock.TryTake(project));

        Directory.Delete(ignore);
        using var again = ProjectLock.TryTake(project);
        Assert.NotNull(again);
    }
}

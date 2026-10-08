using IDevelop.Projects;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

public sealed class ProjectFoldersTests
{
    [CaseSensitiveFact]
    public void Folders_named_Repo_and_repo_on_a_case_sensitive_volume_keep_separate_identities()
    {
        using var temp = new TempFolder();
        var upper = temp.Create("Repo");
        var lower = temp.Create("repo");

        Assert.Equal(upper, ProjectFolders.OnDisk(upper));
        Assert.Equal(lower, ProjectFolders.OnDisk(lower));
    }

    [Fact]
    public void A_folder_named_in_another_case_or_with_a_trailing_separator_resolves_to_its_spelling_on_disk()
    {
        using var temp = new TempFolder();
        var folder = temp.Create("Repo");

        Assert.Equal(folder, ProjectFolders.OnDisk(Path.Combine(Path.GetDirectoryName(folder)!, "REPO") + Path.DirectorySeparatorChar));
    }
}

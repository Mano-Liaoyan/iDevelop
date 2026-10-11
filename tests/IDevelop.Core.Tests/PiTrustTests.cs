using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

/// <summary>
/// Pi loads a project's own skills only in a folder the person trusted in Pi (#93, owner decision 2026-10-11). iDevelop
/// reads Pi's trust store to say so, without a model turn, and never writes it. Every folder here is a fake home.
/// </summary>
public sealed class PiTrustTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly UserFolders _user;
    private readonly string _project;

    public PiTrustTests()
    {
        var home = _temp.Create("home");
        _user = new UserFolders(home, Path.Combine(home, ".pi", "agent"));
        _project = _temp.Create("work/project");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_project_without_its_own_skills_hides_nothing()
    {
        Assert.False(PiTrust.HidesProjectSkills(_project, _user));
        // The person's own ~/.agents/skills is not a project's, even when the project is the home folder.
        Directory.CreateDirectory(Path.Combine(_user.Home, ".agents", "skills"));
        Assert.False(PiTrust.HidesProjectSkills(_user.Home, _user));
    }

    [Theory]
    [InlineData(".agents/skills")]
    [InlineData(".pi/skills")]
    [InlineData("../.agents/skills")]
    public void A_project_with_its_own_skills_hides_them_until_the_folder_is_trusted(string skills)
    {
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(_project, skills)));

        Assert.True(PiTrust.HidesProjectSkills(_project, _user));

        Trust($$"""{"{{Json(_project)}}": true}""");
        Assert.False(PiTrust.HidesProjectSkills(_project, _user));
    }

    [Fact]
    public void The_nearest_decision_counts_and_a_trusted_parent_trusts_its_folders()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".agents", "skills"));
        var work = Path.GetDirectoryName(_project)!;

        Trust($$"""{"{{Json(work)}}": true}""");
        Assert.False(PiTrust.HidesProjectSkills(_project, _user));

        Trust($$"""{"{{Json(work)}}": true, "{{Json(_project)}}": false}""");
        Assert.True(PiTrust.HidesProjectSkills(_project, _user));

        var before = File.ReadAllBytes(Path.Combine(_user.PiAgent, "trust.json"));
        PiTrust.HidesProjectSkills(_project, _user);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_user.PiAgent, "trust.json")));
    }

    [Fact]
    public void Pi_set_to_always_trust_hides_nothing_unless_the_folder_says_otherwise()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".pi", "skills"));
        Directory.CreateDirectory(_user.PiAgent);
        File.WriteAllText(Path.Combine(_user.PiAgent, "settings.json"), """{"defaultProjectTrust": "always"}""");

        Assert.False(PiTrust.HidesProjectSkills(_project, _user));

        Trust($$"""{"{{Json(_project)}}": false}""");
        Assert.True(PiTrust.HidesProjectSkills(_project, _user));
    }

    [Fact]
    public void A_trust_store_pi_cannot_read_trusts_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".agents", "skills"));
        Trust("not json");

        Assert.True(PiTrust.HidesProjectSkills(_project, _user));
    }

    [Fact]
    public void The_agent_folder_is_pis_own_variable_or_the_homes()
    {
        Assert.Equal(Path.Combine("/home/me", ".pi", "agent"), UserFolders.From("/home/me", null).PiAgent);
        Assert.Equal("/opt/pi", UserFolders.From("/home/me", "/opt/pi").PiAgent);
        Assert.Equal(Path.Combine("/home/me", "pi-agent"), UserFolders.From("/home/me", "~/pi-agent").PiAgent);
    }

    private void Trust(string json)
    {
        Directory.CreateDirectory(_user.PiAgent);
        File.WriteAllText(Path.Combine(_user.PiAgent, "trust.json"), json);
    }

    private static string Json(string path) => PiTrust.RealPath(path).Replace("\\", "\\\\", StringComparison.Ordinal);
}

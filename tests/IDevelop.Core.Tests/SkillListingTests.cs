using System.Text.Json;
using IDevelop.Clients;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

/// <summary>
/// A node's skill picker lists the skills its client loads on this machine (#93), as the client itself lists them without
/// a model turn, in the project's folder: its personal, plugin, and project skills, and its built-in skills where the
/// listing can tell them apart. The fake clients answer from fake skill folders, and only names and descriptions are read.
/// </summary>
public sealed class SkillListingTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _personal;

    public SkillListingTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _personal = _temp.Create("home-skills");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Claude_code_lists_its_skills_with_its_bundled_ones_in_the_project_after_it_initializes()
    {
        Skill(_personal, "find-skills", "Finds skills to install.");
        Skill(Path.Combine(_project, ".claude", "skills"), "verify-idevelop", "Drives the built app's real window.");
        var folder = _temp.Path("listing-folder.txt");
        var arguments = _temp.Path("listing-arguments.json");
        _fakes.Install("claude", FakeAgents.ClaudeSignedIn,
            On("-p").Lacking("--include-partial-messages").RecordWorkingDirectory(folder).RecordArguments(arguments)
                .WaitForLine("initialize").ListSkills("claude", [".claude/skills", _personal], builtIn: ["compact", "simplify"])
                .WaitForLine("reload_skills").ListSkills("claude-skills", [".claude/skills", _personal], builtIn: ["simplify"]));
        var directory = await _fakes.DiscoverAsync();

        var listing = await ListedAsync(directory, ClientId.ClaudeCode, _project);

        Assert.Equal(new SkillListing.Listed(
        [
            new SkillOption("find-skills", "Finds skills to install."),
            new SkillOption("simplify", "Built-in simplify"),
            new SkillOption("verify-idevelop", "Drives the built app's real window."),
        ]), listing);
        Assert.False(((SkillListing.Listed)listing).LeavesOutBuiltIns);
        Assert.Equal(Path.GetFullPath(_project), File.ReadAllText(folder));
        Assert.Equal(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"], Read<string[]>(arguments));
        Assert.True(SkillChoices.CanChoose(ClientId.ClaudeCode));
    }

    [Fact]
    public async Task A_claude_code_that_cannot_reload_its_skills_lists_its_initialize_commands_without_its_own()
    {
        Skill(_personal, "find-skills", "Finds skills to install.");
        _fakes.Install("claude", FakeAgents.ClaudeSignedIn,
            On("-p").Lacking("--include-partial-messages")
                .WaitForLine("initialize").ListSkills("claude", [".claude/skills", _personal], builtIn: ["compact", "simplify"])
                .WaitForLine("reload_skills")
                .EchoId("""{"type":"control_response","response":{"subtype":"error","request_id":$id,"error":"Unsupported control request subtype: reload_skills"}}"""));
        var directory = await _fakes.DiscoverAsync();

        var listing = Assert.IsType<SkillListing.Listed>(await ListedAsync(directory, ClientId.ClaudeCode, _project));

        Assert.Equal(new SkillListing.Listed([new SkillOption("find-skills", "Finds skills to install.")]) { LeavesOutBuiltIns = true }, listing);
        Assert.NotEqual(new SkillListing.Listed(listing.Skills), listing);
    }

    [Fact]
    public async Task Codex_lists_its_skills_for_the_project_folder_without_the_ones_its_configuration_turned_off()
    {
        Skill(_personal, "imagegen", "Makes images.");
        Skill(_personal, "openai-docs", "Reads OpenAI's documentation.");
        Skill(Path.Combine(_project, ".agents", "skills"), "release", "Cuts a release.");
        var arguments = _temp.Path("listing-arguments.json");
        _fakes.Install("codex", FakeAgents.CodexModels, FakeAgents.CodexSignedIn,
            On("app-server").Thread("skills/list").RecordArguments(arguments).ListSkills("codex", [".agents/skills", _personal], disabled: ["openai-docs"])
                .AnswerEach("skills/list", On().ListSkills("codex", [".agents/skills", _personal], disabled: ["openai-docs"])));
        var directory = await _fakes.DiscoverAsync();

        var listing = await ListedAsync(directory, ClientId.Codex, _project);

        Assert.Equal(new SkillListing.Listed([new SkillOption("imagegen", "Makes images."), new SkillOption("release", "Cuts a release.")]), listing);
        Assert.Equal(["app-server"], Read<string[]>(arguments));
        using var request = JsonDocument.Parse(File.ReadAllText(arguments + ".thread.json"));
        Assert.Equal([Path.GetFullPath(_project)], request.RootElement.GetProperty("params").GetProperty("cwds").EnumerateArray().Select(cwd => cwd.GetString()));
        Assert.False(((SkillListing.Listed)listing).LeavesOutBuiltIns);
    }

    [Fact]
    public async Task Codex_lists_again_until_the_skills_of_the_remote_plugins_it_loads_in_the_background_are_in_and_the_list_holds()
    {
        Skill(_personal, "imagegen", "Makes images.");
        var remote = _temp.Create("remote-plugins");
        Skill(remote, "pages-write-page", "Writes a page.", name: "pages:write-page");
        var frames = _temp.Path("listing-frames.txt");
        string[] folders = [".agents/skills", _personal];
        // As the real Codex 0.160.1 does, the remote plugins' skills join the list about a second after the app-server starts,
        // and no notification says when.
        _fakes.Install("codex", FakeAgents.CodexModels, FakeAgents.CodexSignedIn,
            On("app-server").Thread("skills/list").RecordFrames(frames).ListSkills("codex", folders, late: [remote], lateAfterMs: 900)
                .AnswerEach("skills/list", On().ListSkills("codex", folders, late: [remote], lateAfterMs: 900)));
        var directory = await _fakes.DiscoverAsync();

        var listing = await ListedAsync(directory, ClientId.Codex, _project);

        Assert.Equal(new SkillListing.Listed([new SkillOption("imagegen", "Makes images."), new SkillOption("pages:write-page", "Writes a page.")]), listing);
        Assert.True(File.ReadAllLines(frames).Count(frame => frame.Contains("skills/list", StringComparison.Ordinal)) >= 2, "Codex was asked once.");
    }

    [Fact]
    public async Task Pi_lists_its_skill_commands_with_their_files_and_not_its_prompt_templates()
    {
        Skill(_personal, "find-skills", "Finds skills to install.");
        Skill(Path.Combine(_project, ".pi", "skills"), "deploy", "Deploys the site.");
        _fakes.Install("pi", FakeAgents.PiRpc(_personal), FakeAgents.PiProvider("deepseek", "ready"), FakeAgents.PiProvider("openai-codex", "ready"));
        var directory = await _fakes.DiscoverAsync();

        var listing = Assert.IsType<SkillListing.Listed>(await ListedAsync(directory, ClientId.Pi, _project));

        Assert.Equal(["deploy", "find-skills"], listing.Skills.Select(skill => skill.Name));
        Assert.Equal(
            [Path.Combine(Path.GetFullPath(_project), ".pi", "skills", "deploy", "SKILL.md"), Path.Combine(_personal, "find-skills", "SKILL.md")],
            listing.Skills.Select(skill => skill.Path));
        Assert.Equal("Finds skills to install.", listing.Skills[1].Description);
    }

    [Fact]
    public async Task Pis_listing_says_when_it_leaves_out_the_projects_own_skills_until_the_folder_is_trusted_in_pi()
    {
        Skill(_personal, "find-skills", "Finds skills to install.");
        Directory.CreateDirectory(Path.Combine(_project, ".agents", "skills"));
        // The real Pi lists no project skill in a folder it does not trust, so this one lists only the person's own.
        _fakes.Install("pi", On("--mode", "rpc", "--no-session").Answer(
                ("get_available_models", On().Replay(Fixture.Path("pi-rpc-models.jsonl"))),
                ("get_commands", On().ListSkills("pi", [_personal]))).WaitForStdinEnd(),
            FakeAgents.PiProvider("deepseek", "ready"), FakeAgents.PiProvider("openai-codex", "ready"));
        var directory = await _fakes.DiscoverAsync();

        var untrusted = Assert.IsType<SkillListing.Listed>(await ListedAsync(directory, ClientId.Pi, _project));
        Assert.True(untrusted.ProjectNotTrusted);
        Assert.Equal(["find-skills"], untrusted.Skills.Select(skill => skill.Name));

        var trust = Path.Combine(_fakes.User.PiAgent, "trust.json");
        Directory.CreateDirectory(_fakes.User.PiAgent);
        File.WriteAllText(trust, JsonSerializer.Serialize(new Dictionary<string, bool> { [PiTrust.RealPath(_project)] = true }));
        await directory.RefreshAsync();
        var trusted = await UntilAsync(directory, ClientId.Pi, _project, listing => listing is SkillListing.Listed { ProjectNotTrusted: false });

        Assert.False(Assert.IsType<SkillListing.Listed>(trusted).ProjectNotTrusted);
    }

    [Fact]
    public async Task Antigravity_has_no_listing_and_a_client_that_is_not_ready_says_why()
    {
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var directory = new ClientDirectory(_fakes.Resolver);

        Assert.Equal(new SkillListing.Checking(), directory.Skills(ClientId.ClaudeCode, _project));
        Assert.Equal(new SkillListing.Unsupported(), directory.Skills(ClientId.Antigravity, _project));
        Assert.False(SkillChoices.CanChoose(ClientId.Antigravity));

        await directory.RefreshAsync();

        Assert.Equal(new SkillListing.Unavailable("Claude Code is not signed in. Run claude in a terminal and sign in."), directory.Skills(ClientId.ClaudeCode, _project));
        Assert.Equal(new SkillListing.Unavailable("No pi command was found on PATH."), directory.Skills(ClientId.Pi, _project));
    }

    [Fact]
    public async Task A_folders_listing_lasts_until_the_clients_next_refresh_and_stays_until_the_new_one_is_in()
    {
        _fakes.LaunchFolder = _temp.Create("launches");
        Skill(Path.Combine(_project, ".claude", "skills"), "verify-idevelop", "Drives the built app's real window.");
        FakeAgents.Install(_fakes, ClientId.ClaudeCode);
        var directory = await _fakes.DiscoverAsync();
        var other = _temp.Create("other");

        await ListedAsync(directory, ClientId.ClaudeCode, _project);
        await ListedAsync(directory, ClientId.ClaudeCode, _project);
        Assert.Equal(1, Listings());

        Assert.Equal(["simplify"], Names(await ListedAsync(directory, ClientId.ClaudeCode, other)));
        Assert.Equal(2, Listings());

        Skill(Path.Combine(_project, ".claude", "skills"), "release", "Cuts a release.");
        await directory.RefreshAsync();

        // A start while the client lists its skills again still has the last listing.
        Assert.Equal(["simplify", "verify-idevelop"], Names(directory.Skills(ClientId.ClaudeCode, _project)));
        var relisted = await UntilAsync(directory, ClientId.ClaudeCode, _project, listing => Names(listing).Contains("release"));
        Assert.Equal(["release", "simplify", "verify-idevelop"], Names(relisted));
        Assert.Equal(3, Listings());
    }

    private static string[] Names(SkillListing listing) => [.. Assert.IsType<SkillListing.Listed>(listing).Skills.Select(skill => skill.Name)];

    [Fact]
    public async Task A_listing_the_client_refuses_says_why()
    {
        _fakes.Install("claude", FakeAgents.ClaudeSignedIn,
            On("-p").Lacking("--include-partial-messages").WaitForLine("initialize")
                .EchoId("""{"type":"control_response","response":{"subtype":"error","request_id":$id,"error":"settings are invalid"}}"""));
        var directory = await _fakes.DiscoverAsync();

        Assert.Equal(
            new SkillListing.Unavailable("Claude Code did not list its skills: settings are invalid."),
            await ListedAsync(directory, ClientId.ClaudeCode, _project));
    }

    private int Listings() => Directory.EnumerateFiles(_fakes.LaunchFolder!, "*.json")
        .Count(file => Read<string[]>(file) is ["-p", _, _, _, _, "--verbose"]);

    private static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(file))!;

    /// <summary>A skill folder whose SKILL.md names it <paramref name="name"/>, or its folder's name.</summary>
    internal static void Skill(string folder, string directory, string description, string? name = null)
    {
        var skill = Directory.CreateDirectory(Path.Combine(folder, directory)).FullName;
        name ??= directory;
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\nInstructions iDevelop never reads.\n");
    }

    /// <summary>The listing once it is in, which the directory's change says.</summary>
    internal static Task<SkillListing> ListedAsync(ClientDirectory directory, ClientId client, string folder) =>
        UntilAsync(directory, client, folder, listing => listing is not SkillListing.Checking);

    /// <summary>The listing once <paramref name="done"/> accepts it, checked at each of the directory's changes.</summary>
    private static async Task<SkillListing> UntilAsync(ClientDirectory directory, ClientId client, string folder, Func<SkillListing, bool> done)
    {
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (done(directory.Skills(client, folder)))
            {
                listed.TrySetResult();
            }
        }

        directory.Changed += OnChanged;
        try
        {
            if (!done(directory.Skills(client, folder)))
            {
                await listed.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }

            return directory.Skills(client, folder);
        }
        finally
        {
            directory.Changed -= OnChanged;
        }
    }
}

using System.Text.Json;
using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

public sealed class ProtocolLaunchTests
{
    private static readonly string[] ClaudeEditNone = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "bypassPermissions", "--resume", "session-1", "--permission-prompts", "none"];
    private static readonly string[] ClaudePlanNone = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "plan", "--resume", "session-1", "--permission-prompts", "none"];
    private static readonly string[] ClaudeEditHost = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "bypassPermissions", "--resume", "session-1", "--permission-prompt-tool", "stdio"];
    private static readonly string[] ClaudePlanHost = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "plan", "--resume", "session-1", "--permission-prompt-tool", "stdio"];
    private static readonly string[] Codex = ["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"];
    private static readonly string[] Pi = ["-p", "--mode", "json", "--model", "m", "--thinking", "high", "--session-id", "session-1"];
    private static readonly string[] AgyEdit = ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "m", "--effort", "high", "--mode", "accept-edits", "--print=", "--conversation", "session-1"];
    private static readonly string[] AgyPlan = ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "m", "--effort", "high", "--mode", "plan", "--print=", "--conversation", "session-1"];

    [Fact]
    public void Every_client_launches_with_literal_arguments_for_each_policy_row()
    {
        (bool ReadOnly, ConversationMode Mode, bool Reviewer, string[] Claude, string[] Agy)[] rows =
        [
            (false, ConversationMode.Autonomous, false, ClaudeEditNone, AgyEdit),
            (true, ConversationMode.Autonomous, false, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.MayAsk, false, ClaudeEditHost, AgyEdit),
            (true, ConversationMode.MayAsk, false, ClaudePlanHost, AgyPlan),
            (false, ConversationMode.Chat, false, ClaudeEditHost, AgyEdit),
            (true, ConversationMode.Chat, false, ClaudePlanHost, AgyPlan),
            (false, ConversationMode.Autonomous, true, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.MayAsk, true, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.Chat, true, ClaudePlanNone, AgyPlan),
            (true, ConversationMode.Chat, true, ClaudePlanNone, AgyPlan),
        ];
        foreach (var row in rows)
        {
            foreach (var client in ClientCatalog.All)
            {
                var request = new LaunchRequest("m", "high", "banana")
                {
                    ReadOnly = row.ReadOnly, ResumeSession = "session-1",
                    Policy = ClientPolicy.For(client, row.ReadOnly, row.Mode, row.Reviewer),
                };
                var actual = ClientCatalog.Get(client).Launch(request);
                Assert.Equal(client switch
                {
                    ClientId.ClaudeCode => row.Claude,
                    ClientId.Codex => Codex,
                    ClientId.Pi => Pi,
                    ClientId.Antigravity => row.Agy,
                }, actual.Arguments.ToArray());
            }
        }
    }

    [Fact]
    public void Fresh_turns_without_effort_have_literal_arguments_and_one_shot_input()
    {
        Assert.Equal(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--permission-mode", "bypassPermissions", "--permission-prompts", "none"],
            ClientCatalog.Get(ClientId.ClaudeCode).Launch(new LaunchRequest("m", null, "banana")).Arguments.ToArray());
        Assert.Equal(["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"],
            ClientCatalog.Get(ClientId.Codex).Launch(new LaunchRequest("m", null, "banana")).Arguments.ToArray());
        var pi = ClientCatalog.Get(ClientId.Pi).Protocol(new LaunchRequest("m", null, "banana")).Start();
        Assert.Equal(["banana"], pi.Writes.ToArray());
        Assert.True(pi.CloseInput);
        var agy = ClientCatalog.Get(ClientId.Antigravity).Protocol(new LaunchRequest("m", null, "banana")).Start();
        Assert.Equal(["""{"event":"user","message":{"role":"user","content":"banana"}}""" + "\n"], agy.Writes.ToArray());
        Assert.True(agy.CloseInput);
    }

    private static readonly SkillOption Tdd = new("tdd", "Test first.") { Path = "/home/me/.pi/agent/skills/tdd/SKILL.md" };
    private static readonly SkillOption ImageGen = new("imagegen", "Makes images.") { Path = "/home/me/.agents/skills/imagegen/SKILL.md" };

    [Fact]
    public void A_skill_choice_reaches_claude_code_as_its_initialize_allow_list_on_every_turn()
    {
        foreach (var session in new[] { null, "session-1" })
        {
            var request = new LaunchRequest("m", "high", "banana") { ResumeSession = session, Skills = new SkillLaunch([Tdd], [ImageGen]) };

            Assert.Equal(["""{"type":"control_request","request_id":"init-1","request":{"subtype":"initialize","skills":["tdd"]}}"""],
                ClientCatalog.Get(ClientId.ClaudeCode).Protocol(request).Start().Writes.ToArray());
            Assert.Equal(ClientCatalog.Get(ClientId.ClaudeCode).Launch(request with { Skills = null }).Arguments.ToArray(), ClientCatalog.Get(ClientId.ClaudeCode).Launch(request).Arguments.ToArray());
        }

        Assert.Equal(["""{"type":"control_request","request_id":"init-1","request":{"subtype":"initialize","skills":[]}}"""],
            ClientCatalog.Get(ClientId.ClaudeCode).Protocol(new LaunchRequest("m", "high", "banana") { Skills = new SkillLaunch([], [Tdd]) }).Start().Writes.ToArray());
    }

    [Fact]
    public void A_skill_choice_reaches_codex_as_the_skills_it_turns_off_in_the_threads_configuration()
    {
        (string? Session, string Method)[] launches = [(null, "thread/start"), ("session-1", "thread/resume")];
        foreach (var (session, method) in launches)
        {
            var request = new LaunchRequest("m", "high", "banana") { ResumeSession = session, Skills = new SkillLaunch([Tdd], [ImageGen]) };

            using var thread = ThreadFrame(request);

            Assert.Equal(method, thread.RootElement.GetProperty("method").GetString());
            Assert.Equal("""{"skills.config":[{"name":"imagegen","enabled":false}]}""", thread.RootElement.GetProperty("params").GetProperty("config").GetRawText());
            Assert.Equal(Codex, ClientCatalog.Get(ClientId.Codex).Launch(request).Arguments.ToArray());
        }

        using var all = ThreadFrame(new LaunchRequest("m", "high", "banana") { Skills = new SkillLaunch([Tdd, ImageGen], []) });
        Assert.False(all.RootElement.GetProperty("params").TryGetProperty("config", out _));
    }

    [Fact]
    public void A_skill_choice_reaches_pi_as_no_discovered_skills_and_each_chosen_skills_file()
    {
        var request = new LaunchRequest("m", "high", "banana") { ResumeSession = "session-1", Skills = new SkillLaunch([Tdd, ImageGen with { Name = "zz" }], [ImageGen]) };

        Assert.Equal(["-p", "--mode", "json", "--model", "m", "--thinking", "high", "--no-skills", "--skill", Tdd.Path!, "--skill", ImageGen.Path!, "--session-id", "session-1"],
            ClientCatalog.Get(ClientId.Pi).Launch(request).Arguments.ToArray());
        Assert.Equal(["-p", "--mode", "json", "--model", "m", "--no-skills"],
            ClientCatalog.Get(ClientId.Pi).Launch(new LaunchRequest("m", null, "banana") { Skills = new SkillLaunch([], [Tdd]) }).Arguments.ToArray());
    }

    [Fact]
    public void An_unchecked_choice_reaches_claude_code_by_its_names_and_leaves_codex_and_pi_their_own_skills()
    {
        // The skill list could not be read, so the choice holds its names alone, with no files and no other skills.
        var request = new LaunchRequest("m", "high", "banana")
        {
            ResumeSession = "session-1", Skills = new SkillLaunch([new SkillOption("gone", ""), new SkillOption("tdd", "")], []) { Unchecked = true },
        };

        Assert.Equal(["""{"type":"control_request","request_id":"init-1","request":{"subtype":"initialize","skills":["gone","tdd"]}}"""],
            ClientCatalog.Get(ClientId.ClaudeCode).Protocol(request).Start().Writes.ToArray());
        using var thread = ThreadFrame(request);
        Assert.False(thread.RootElement.GetProperty("params").TryGetProperty("config", out _));
        Assert.Equal(Pi, ClientCatalog.Get(ClientId.Pi).Launch(request).Arguments.ToArray());
    }

    [Fact]
    public void Without_a_choice_no_client_is_told_anything_about_skills()
    {
        var request = new LaunchRequest("m", "high", "banana") { ResumeSession = "session-1" };

        Assert.Equal(["""{"type":"control_request","request_id":"init-1","request":{"subtype":"initialize"}}"""],
            ClientCatalog.Get(ClientId.ClaudeCode).Protocol(request).Start().Writes.ToArray());
        using var thread = ThreadFrame(request);
        Assert.False(thread.RootElement.GetProperty("params").TryGetProperty("config", out _));
        Assert.Equal(Pi, ClientCatalog.Get(ClientId.Pi).Launch(request).Arguments.ToArray());
        var agy = ClientCatalog.Get(ClientId.Antigravity).Launch(request with { Skills = new SkillLaunch([Tdd], [ImageGen]) });
        Assert.Equal(AgyEdit, agy.Arguments.ToArray());
    }

    private static JsonDocument ThreadFrame(LaunchRequest request)
    {
        var protocol = ClientCatalog.Get(ClientId.Codex).Protocol(request);
        protocol.Start();
        return JsonDocument.Parse(protocol.Read("""{"id":"init-1","result":{}}""").Writes[1]);
    }
}

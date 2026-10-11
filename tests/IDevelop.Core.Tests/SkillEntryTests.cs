using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

/// <summary>The entries of a node's skill picker (#93).</summary>
public sealed class SkillEntryTests
{
    private static readonly ImmutableArray<SkillOption> Listed =
    [
        new("code-review", "Reviews."), new("code-review:code-review", "Reviews a PR."), new("probe-alpha", "Alpha."),
        new("probeplug:probe-alpha", "Plugin alpha."), new("probeplug:probe-delta", "Plugin delta."),
    ];

    // Claude Code matches a chosen name exactly or as the ":name" ending of a plugin skill (probe and owner decision, 2026-10-11).
    [Fact]
    public void A_chosen_bare_name_says_which_plugin_skills_claude_code_lets_in_with_it()
    {
        var entries = SkillChoices.Entries(ClientId.ClaudeCode, ["code-review", "probe-alpha", "probeplug:probe-delta"], Listed).ToDictionary(entry => entry.Name);

        Assert.Equal(["code-review:code-review"], entries["code-review"].LetsIn.ToArray());
        Assert.Equal(["probeplug:probe-alpha"], entries["probe-alpha"].LetsIn.ToArray());
        Assert.All(new[] { "code-review:code-review", "probeplug:probe-alpha", "probeplug:probe-delta" }, name => Assert.Empty(entries[name].LetsIn));
    }

    [Fact]
    public void A_name_that_is_not_chosen_or_another_clients_choice_lets_nothing_in()
    {
        Assert.All(SkillChoices.Entries(ClientId.ClaudeCode, null, Listed), entry => Assert.Empty(entry.LetsIn));
        Assert.All(SkillChoices.Entries(ClientId.ClaudeCode, ["probeplug:probe-alpha"], Listed), entry => Assert.Empty(entry.LetsIn));
        Assert.All(SkillChoices.Entries(ClientId.Codex, ["code-review", "probe-alpha"], Listed), entry => Assert.Empty(entry.LetsIn));
        Assert.All(SkillChoices.Entries(ClientId.Pi, ["code-review", "probe-alpha"], Listed), entry => Assert.Empty(entry.LetsIn));
    }
}

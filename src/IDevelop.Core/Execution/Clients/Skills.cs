using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>A skill that a client loads, as the client itself lists it: its name and description, never its contents.</summary>
public sealed record SkillOption(string Name, string Description)
{
    /// <summary>Pi only: the skill file that its <c>--skill</c> option loads.</summary>
    internal string? Path { get; init; }
}

/// <summary>The skills that one client loads in one project folder, as the client lists them without a model turn.</summary>
public abstract record SkillListing
{
    private SkillListing() { }

    public sealed record Checking : SkillListing;

    /// <summary>Each name once, in the order the picker shows them.</summary>
    public sealed record Listed(ImmutableArray<SkillOption> Skills) : SkillListing
    {
        /// <summary>
        /// The listing could not tell the client's built-in skills from its built-in commands, so it left them all out, as
        /// a Claude Code without <c>reload_skills</c> does. A choice then leaves the built-in skills out too.
        /// </summary>
        public bool LeavesOutBuiltIns { get; init; }

        /// <summary>
        /// The client leaves out the project's own skills because the person has not trusted the folder in it, as Pi does
        /// (#93, owner decision 2026-10-11). They appear once the folder is trusted.
        /// </summary>
        public bool ProjectNotTrusted { get; init; }

        // ImmutableArray compares by reference, so equality compares the skills themselves.
        public bool Equals(Listed? other) => other is not null && Skills.SequenceEqual(other.Skills) && LeavesOutBuiltIns == other.LeavesOutBuiltIns
            && ProjectNotTrusted == other.ProjectNotTrusted;

        public override int GetHashCode() => Skills.Length;
    }

    /// <summary>The client is not ready, or its listing failed. The reason is for the person.</summary>
    public sealed record Unavailable(string Reason) : SkillListing;

    /// <summary>The client cannot limit its skills per task, as Antigravity CLI cannot. It uses all of them.</summary>
    public sealed record Unsupported : SkillListing;
}

/// <summary>How a client lists the skills it loads in a folder without a model turn.</summary>
internal sealed record SkillSource(Func<string, Probe> Probe, Func<ProbeOutput, SkillListing> Parse)
{
    /// <summary>What the listing adds from the person's own folders about the project folder, such as Pi's trust in it.</summary>
    public Func<SkillListing, string, UserFolders, SkillListing>? Annotate { get; init; }
}

/// <summary>What one launch passes for a node's choice: the chosen skills that the client lists, and the ones it lists that were not chosen.</summary>
internal sealed record SkillLaunch(ImmutableArray<SkillOption> Chosen, ImmutableArray<SkillOption> Others)
{
    /// <summary>
    /// The client's skill list could not be read, so <see cref="Chosen"/> holds the chosen names as they are, with no files,
    /// and <see cref="Others"/> is empty. Claude Code takes the names. Codex, which needs the others to turn them off, and
    /// Pi, which needs the files, are told nothing and use all of their skills.
    /// </summary>
    public bool Unchecked { get; init; }
}

/// <summary>One entry of a node's skill picker. A chosen skill that the client no longer lists stays, not installed.</summary>
public sealed record SkillEntry(string Name, string Description, bool Chosen, bool Installed)
{
    /// <summary>The listed plugin skills that the client lets in with this chosen name, as Claude Code does by a name's ending.</summary>
    public ImmutableArray<string> LetsIn { get; init; } = [];

    public bool Equals(SkillEntry? other) => other is not null && (Name, Description, Chosen, Installed) == (other.Name, other.Description, other.Chosen, other.Installed)
        && LetsIn.SequenceEqual(other.LetsIn);

    public override int GetHashCode() => HashCode.Combine(Name, Chosen, Installed);
}

/// <summary>The rules that the skill picker and the start share. None of them is a graph rule.</summary>
public static class SkillChoices
{
    /// <summary>
    /// What a choice leaves to the client anyway, which the picker says: Claude Code's subagents take their skills from their
    /// own definitions, so they can still use every skill (probe and owner decision, 2026-10-11). Null for another client.
    /// </summary>
    public static string? Beyond(ClientId client) => client == ClientId.ClaudeCode ? "Claude Code's subagents can still use every skill." : null;

    /// <summary>Whether the client can limit a task's skills. Antigravity CLI cannot.</summary>
    public static bool CanChoose(ClientId client) => Clients.Get(client).Skills is not null;

    /// <summary>
    /// The listed skills, each ticked when the node has no choice or chose it, then each chosen skill that the client no
    /// longer lists.
    /// </summary>
    public static ImmutableArray<SkillEntry> Entries(ClientId client, ImmutableSortedSet<string>? chosen, ImmutableArray<SkillOption> listed)
    {
        ImmutableArray<SkillEntry> installed = [.. listed.Select(skill => new SkillEntry(skill.Name, skill.Description, chosen is null || chosen.Contains(skill.Name), true)
        {
            LetsIn = LetsIn(client, chosen, skill.Name, listed),
        })];
        return [.. installed, .. Missing(chosen, listed).Select(name => new SkillEntry(name, "", true, false))];
    }

    /// <summary>
    /// Claude Code's allow-list matches a skill's full name or a ":name" ending of it, so a chosen bare name also lets in
    /// each plugin skill that ends with it (probe and owner decision, 2026-10-11). The picker says so on the chosen skill.
    /// </summary>
    private static ImmutableArray<string> LetsIn(ClientId client, ImmutableSortedSet<string>? chosen, string name, ImmutableArray<SkillOption> listed) =>
        client == ClientId.ClaudeCode && chosen is not null && chosen.Contains(name) && !name.Contains(':', StringComparison.Ordinal)
            ? [.. listed.Select(skill => skill.Name).Where(other => other.EndsWith(":" + name, StringComparison.Ordinal))]
            : [];

    /// <summary>
    /// The choice after the person ticks or unticks <paramref name="name"/>. With no choice yet every listed skill counts
    /// as ticked, so the first untick keeps exactly the others. Unticking a skill that is not installed removes it.
    /// </summary>
    public static ImmutableSortedSet<string> Toggle(ImmutableSortedSet<string>? chosen, ImmutableArray<SkillOption> listed, string name, bool tick)
    {
        var current = chosen ?? ImmutableSortedSet.CreateRange(StringComparer.Ordinal, listed.Select(skill => skill.Name));
        return tick ? current.Add(name) : current.Remove(name);
    }

    /// <summary>The chosen skills that the client does not list, in order.</summary>
    public static ImmutableArray<string> Missing(ImmutableSortedSet<string>? chosen, ImmutableArray<SkillOption> listed) =>
        chosen is null ? [] : [.. chosen.Where(name => !listed.Any(skill => skill.Name == name))];

    /// <summary>The launch of a choice whose skill list could not be read: its names as they are (#93, 2026-10-11).</summary>
    internal static SkillLaunch Unchecked(ImmutableSortedSet<string> chosen) => new([.. chosen.Select(name => new SkillOption(name, ""))], []) { Unchecked = true };

    /// <summary>
    /// What a task with a choice gets when its client's skill list could not be read, which the Skills row and the attempt's
    /// activity say. The owner decided on 2026-10-11 that Claude Code gets the chosen names unchecked, and that Codex and Pi,
    /// whose choice needs the list, run with all of their skills.
    /// </summary>
    public static string UncheckedNote(ClientId client) => client switch
    {
        ClientId.Codex or ClientId.Pi =>
            $"{Clients.Name(client)}'s skill list couldn't be read, so the skill choice couldn't be applied, and {Clients.Name(client)} runs with all of its skills.",
        _ => $"{Clients.Name(client)}'s skill list couldn't be read, so it gets the chosen skills as they are, unchecked.",
    };

    /// <summary>A launch takes only the chosen skills the client lists, so a skill that is not installed is left out.</summary>
    internal static SkillLaunch Launch(ImmutableSortedSet<string> chosen, ImmutableArray<SkillOption> listed) =>
        new([.. listed.Where(skill => chosen.Contains(skill.Name))], [.. listed.Where(skill => !chosen.Contains(skill.Name))]);

    /// <summary>Each name once, by name, keeping the first skill the client lists with it.</summary>
    internal static ImmutableArray<SkillOption> Distinct(IEnumerable<SkillOption> skills) =>
        [.. skills.DistinctBy(skill => skill.Name, StringComparer.Ordinal).OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ThenBy(skill => skill.Name, StringComparer.Ordinal)];
}

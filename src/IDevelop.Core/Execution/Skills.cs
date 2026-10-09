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
        // ImmutableArray compares by reference, so equality compares the skills themselves.
        public bool Equals(Listed? other) => other is not null && Skills.SequenceEqual(other.Skills);

        public override int GetHashCode() => Skills.Length;
    }

    /// <summary>The client is not ready, or its listing failed. The reason is for the person.</summary>
    public sealed record Unavailable(string Reason) : SkillListing;

    /// <summary>The client cannot limit its skills per task, as Antigravity CLI cannot. It uses all of them.</summary>
    public sealed record Unsupported : SkillListing;
}

/// <summary>
/// How a client lists the skills it loads in a folder without a model turn. <see cref="ListsBuiltIns"/> is false when the
/// listing cannot tell the client's built-in skills from its built-in commands, so it leaves them out.
/// </summary>
internal sealed record SkillSource(Func<string, Probe> Probe, Func<ProbeOutput, SkillListing> Parse)
{
    public bool ListsBuiltIns { get; init; } = true;
}

/// <summary>What one launch passes for a node's choice: the chosen skills that the client lists, and the ones it lists that were not chosen.</summary>
internal sealed record SkillLaunch(ImmutableArray<SkillOption> Chosen, ImmutableArray<SkillOption> Others);

/// <summary>One entry of a node's skill picker. A chosen skill that the client no longer lists stays, not installed.</summary>
public sealed record SkillEntry(string Name, string Description, bool Chosen, bool Installed);

/// <summary>The rules that the skill picker and the start share. None of them is a graph rule.</summary>
public static class SkillChoices
{
    /// <summary>Whether the client can limit a task's skills. Antigravity CLI cannot.</summary>
    public static bool CanChoose(ClientId client) => Clients.Get(client).Skills is not null;

    /// <summary>Whether the client's listing holds its built-in skills, which a choice can then keep.</summary>
    public static bool ListsBuiltIns(ClientId client) => Clients.Get(client).Skills is { ListsBuiltIns: true };

    /// <summary>
    /// The listed skills, each ticked when the node has no choice or chose it, then each chosen skill that the client no
    /// longer lists.
    /// </summary>
    public static ImmutableArray<SkillEntry> Entries(ImmutableSortedSet<string>? chosen, ImmutableArray<SkillOption> listed)
    {
        ImmutableArray<SkillEntry> installed = [.. listed.Select(skill => new SkillEntry(skill.Name, skill.Description, chosen is null || chosen.Contains(skill.Name), true))];
        return [.. installed, .. Missing(chosen, listed).Select(name => new SkillEntry(name, "", true, false))];
    }

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

    /// <summary>A launch takes only the chosen skills the client lists, so a skill that is not installed is left out.</summary>
    internal static SkillLaunch Launch(ImmutableSortedSet<string> chosen, ImmutableArray<SkillOption> listed) =>
        new([.. listed.Where(skill => chosen.Contains(skill.Name))], [.. listed.Where(skill => !chosen.Contains(skill.Name))]);

    /// <summary>Each name once, by name, keeping the first skill the client lists with it.</summary>
    internal static ImmutableArray<SkillOption> Distinct(IEnumerable<SkillOption> skills) =>
        [.. skills.DistinctBy(skill => skill.Name, StringComparer.Ordinal).OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ThenBy(skill => skill.Name, StringComparer.Ordinal)];
}

using System.Collections.Immutable;
using JetBrains.Annotations;

namespace IDevelop.Git;

internal readonly record struct CommitId(string Hex);

internal readonly record struct TreeId(string Hex);

/// <summary>An index entry: its mode, its object, its merge stage (0 when it is merged), and its path.</summary>
internal sealed record StageEntry([UsedImplicitly] string Mode, [UsedImplicitly] string Object, [UsedImplicitly] int Stage, string Path);

internal sealed record CommitRecipe(TreeId Tree, ImmutableArray<CommitId> Parents, string Message, string Author, string Committer,
    DateTimeOffset Timestamp);

/// <summary>One of <c>git merge-tree --messages</c>' informational messages.</summary>
internal sealed record MergeMessage(ImmutableArray<string> Paths, string Type, string Text);

internal sealed record ConfigEntry(string Key, string Value);


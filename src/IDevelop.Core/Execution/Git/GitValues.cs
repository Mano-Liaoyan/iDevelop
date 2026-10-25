using System.Collections.Immutable;

namespace IDevelop.Execution;

internal readonly record struct CommitId(string Hex);

internal readonly record struct TreeId(string Hex);

/// <summary>An index entry: its mode, its object, its merge stage (0 when it is merged), and its path.</summary>
internal sealed record StageEntry(string Mode, string Object, int Stage, string Path);

internal sealed record CommitRecipe(TreeId Tree, ImmutableArray<CommitId> Parents, string Message, string Author, string Committer,
    DateTimeOffset Timestamp);

/// <summary>Moves <paramref name="Ref"/> to <paramref name="Target"/> when it is at <paramref name="Expected"/>, or absent when that is null.</summary>
internal sealed record RefChange(string Ref, CommitId? Expected, CommitId Target);

/// <summary>One of <c>git merge-tree --messages</c>' informational messages.</summary>
internal sealed record MergeMessage(ImmutableArray<string> Paths, string Type, string Text);

internal sealed record ConfigEntry(string Key, string Value);

/// <summary>
/// What kind of Git failure a read met. Most are plain failures. The rest are the repository's own states that a caller may
/// treat on their own: a folder in no work tree, a ref that is symbolic, and an index that hides changes.
/// </summary>
internal enum GitFault { Failed, NotInWorkTree, SymbolicRef, HiddenChanges }

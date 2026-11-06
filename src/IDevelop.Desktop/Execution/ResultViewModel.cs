using System.Collections.Immutable;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Git;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Execution;

/// <summary>One row of a node's result history in the inspector: its number, its report's first paragraph, and its commit.</summary>
/// <param name="current">The node's latest result, and nothing it rests on changed since.</param>
public sealed class ResultViewModel(NodeResult result, int number, bool current) : ObservableObject
{
    private bool _read;

    public string Heading => current ? $"Result {number} · Current" : $"Result {number}";

    /// <summary>The report's first paragraph, which the row's tooltip gives whole.</summary>
    public string Summary => CardNow.FirstParagraph(ResultBlock.Prose(result.Report)) ?? "No report.";

    /// <summary>The summary as plain text.</summary>
    public string PlainSummary => Plain(Summary);

    public string Report => ResultBlock.Prose(result.Report);

    /// <summary>The card has more to say than the inspector's result: a commit.</summary>
    public bool HasDetail => Commit is not null;

    /// <summary>"Commit 1a2b3c4" for a node that edits the project, or null.</summary>
    public string? Commit => result.Commit is { Length: >= 7 } commit ? $"Commit {commit[..7]}" : null;

    private const int ShortList = 3;

    /// <summary>The files the result's commit changed, once they are read.</summary>
    public ImmutableArray<FileChange> Changes
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasChanges));
                OnPropertyChanged(nameof(ChangeLabel));
                OnPropertyChanged(nameof(Added));
                OnPropertyChanged(nameof(Deleted));
                // A short list costs little room and answers what changed at once.
                IsChangesOpen = value.Length is > 0 and <= ShortList;
            }
        }
    } = [];

    public bool HasChanges => Changes.Length > 0;

    /// <summary>"3 files changed".</summary>
    public string ChangeLabel => Changes.Length == 1 ? "1 file changed" : $"{Changes.Length} files changed";

    /// <summary>"+12", the lines the commit added.</summary>
    public string Added => $"+{Changes.Sum(change => change.Added ?? 0)}";

    /// <summary>"−3", the lines the commit deleted.</summary>
    public string Deleted => $"−{Changes.Sum(change => change.Deleted ?? 0)}";

    /// <summary>Whether the person opened the list of changed files.</summary>
    public bool IsChangesOpen
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Text without Markdown's code and strong marks, for a line that shows it as plain text.</summary>
    internal static string Plain(string text) => text.Replace("`", "").Replace("**", "");

    /// <summary>Reads the commit's changes off the UI thread. A second call and a result with no commit read nothing.</summary>
    internal void ReadChanges(Func<string, ImmutableArray<FileChange>> read)
    {
        if (_read || result.Commit is not { Length: >= 7 } commit)
        {
            return;
        }

        _read = true;
        UiWork.Start(async () => Changes = await Task.Run(() => read(commit)), _ => { });
    }
}

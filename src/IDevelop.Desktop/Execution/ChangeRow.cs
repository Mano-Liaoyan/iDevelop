using System.Collections.Immutable;
using IDevelop.Desktop.Mvvm;
using IDevelop.Git;

namespace IDevelop.Desktop.Execution;

/// <summary>What a line of a file's diff is, which its color shows.</summary>
public enum DiffLineKind { Context, Added, Deleted, Hunk }

/// <summary>One line of a file's diff in a result's card.</summary>
public sealed record DiffLine(string Text, DiffLineKind Kind)
{
    public bool IsAdded => Kind == DiffLineKind.Added;

    public bool IsDeleted => Kind == DiffLineKind.Deleted;

    public bool IsHunk => Kind == DiffLineKind.Hunk;

    internal static DiffLine Of(string text) => new(text, text switch
    {
        ['@', '@', ..] => DiffLineKind.Hunk,
        ['+', ..] => DiffLineKind.Added,
        ['-', ..] => DiffLineKind.Deleted,
        _ => DiffLineKind.Context,
    });
}

/// <summary>One file a result changed. It opens onto its diff, which it reads the first time it opens. A small change starts open.</summary>
public sealed class ChangeRow : ObservableObject
{
    // A change of this many lines costs little room, so it shows at once.
    private const int ShortChange = 12;

    private readonly Func<string, ImmutableArray<string>> _read;
    private bool _started;

    internal ChangeRow(FileChange file, Func<string, ImmutableArray<string>> read, bool startsOpen)
    {
        File = file;
        _read = read;
        if (startsOpen && file.Added + file.Deleted <= ShortChange)
        {
            SetOpen();
        }
    }

    private void SetOpen() => IsOpen = true;

    /// <summary>A row that starts open reads its diff now.</summary>
    internal void Open() => Read();

    public FileChange File { get; }

    public string Path => File.Path;

    public string Added => File.Added is { } added ? $"+{added}" : "";

    public string Deleted => File.Deleted is { } deleted ? $"\u2212{deleted}" : "";

    /// <summary>A binary file has no text lines to show.</summary>
    public bool HasDiff => File.Added is not null;

    public string OpenLabel => IsOpen ? $"Hide the changes in {Path}" : $"Show the changes in {Path}";

    public bool IsOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(OpenLabel));
                if (value)
                {
                    Read();
                }
            }
        }
    }

    public IReadOnlyList<DiffLine> Lines
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    private void Read()
    {
        if (_started || !HasDiff)
        {
            return;
        }

        _started = true;
        UiWork.Start(async () =>
        {
            var lines = await Task.Run(() => _read(Path));
            Lines = [.. lines.Select(DiffLine.Of)];
        }, _ => { });
    }
}

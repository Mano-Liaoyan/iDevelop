using System.Collections.Immutable;
using System.Windows.Input;
using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>A canvas command the Add popover offers beside the blueprints, with its glyph's resource key and its key hint.</summary>
public sealed record AddNodeAction(string Label, string Icon, string Hint, ICommand Command);

/// <summary>A line of the Add popover's list: a section header or a row the person can choose.</summary>
public abstract class AddNodeItem : ObservableObject;

public sealed class AddNodeHeader(string title) : AddNodeItem
{
    public string Title { get; } = title;
}

public abstract class AddNodeRow : AddNodeItem
{
    private bool _isHighlighted;

    private protected AddNodeRow(string label, string description, Action run)
    {
        Label = label;
        Description = description;
        Command = new RelayCommand(run);
    }

    public string Label { get; }

    public string Description { get; }

    /// <summary>What UI Automation calls the row.</summary>
    public virtual string AutomationName => Label;

    /// <summary>Closes the popover and does what the row says.</summary>
    public ICommand Command { get; }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        internal set => SetProperty(ref _isHighlighted, value);
    }
}

/// <summary>A blueprint to place. A library blueprint also offers Edit, and every blueprint offers Derive.</summary>
public sealed class BlueprintRow : AddNodeRow
{
    internal BlueprintRow(BlueprintEntryViewModel entry, Action place, Action<ICommand> run)
        : base(entry.Name, entry.Description, place)
    {
        Blueprint = entry.Blueprint;
        Kind = entry.Kind;
        CanEdit = entry.CanEdit;
        DeriveCommand = new RelayCommand(() => run(entry.DeriveCommand));
        EditCommand = new RelayCommand(() => run(entry.EditCommand));
    }

    public Blueprint Blueprint { get; }

    public NodeKind Kind { get; }

    public bool IsLibrary => !Blueprint.IsBuiltIn;

    public bool CanEdit { get; }

    public override string AutomationName => $"Add {Label}";

    public string DeriveName => $"Derive from {Label}";

    public string EditName => $"Edit {Label}";

    public ICommand DeriveCommand { get; }

    public ICommand EditCommand { get; }
}

public sealed class ActionRow(AddNodeAction action, Action run) : AddNodeRow(action.Label, "", run)
{
    public string Icon { get; } = action.Icon;

    public string Hint { get; } = action.Hint;
}

/// <summary>
/// The Add popover: every placeable blueprint by library, and on empty canvas the canvas actions, filtered by what the
/// person types. Choosing a blueprint places a node for the target in one edit.
/// </summary>
public sealed class AddNodeViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly ImmutableArray<(string Heading, ImmutableArray<BlueprintRow> Rows)> _libraries;
    private readonly ImmutableArray<ActionRow> _actions;
    private string _query = "";
    private IReadOnlyList<AddNodeItem> _items = [];
    private List<AddNodeRow> _rows = [];
    private AddNodeRow? _highlighted;

    /// <summary>Reads the libraries again, so a blueprint saved elsewhere is offered without a reload.</summary>
    internal AddNodeViewModel(WorkflowCanvasViewModel canvas, AddTarget target)
    {
        _canvas = canvas;
        Target = target;
        canvas.Blueprints.Reload();
        _libraries = [.. canvas.Blueprints.Groups.Where(group => !group.IsEmpty).Select(group => (
            Heading(group.Heading),
            group.Entries.Select(entry => new BlueprintRow(entry, () => Run(() => canvas.Add(target, entry.Blueprint)), Run)).ToImmutableArray()))];
        _actions = target is AddTarget.AtPoint
            ? [.. Actions().Select(action => new ActionRow(action, () => Run(action.Command)))]
            : [];
        Problems = canvas.Blueprints.Problems;
        (ConnectLabel, ConnectNode) = target switch
        {
            AddTarget.FromOutput from => ("Connect from", canvas.Nodes.FirstOrDefault(node => node.Id == from.Source)),
            AddTarget.ToInput to => ("Connect to", canvas.Nodes.FirstOrDefault(node => node.Id == to.Target)),
            AddTarget.Between between => ("Insert after", canvas.Nodes.FirstOrDefault(node => node.Id == between.Connection.From)),
            _ => ((string?)null, (TaskNodeViewModel?)null),
        };
        Show();
    }

    public AddTarget Target { get; }

    /// <summary>What the person typed into the search field.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Show();
            }
        }
    }

    /// <summary>Headers and rows in the order the popover lists them.</summary>
    public IReadOnlyList<AddNodeItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>The row Enter chooses, or null when nothing matches.</summary>
    public AddNodeRow? Highlighted
    {
        get => _highlighted;
        set
        {
            var old = _highlighted;
            if (SetProperty(ref _highlighted, value))
            {
                old?.IsHighlighted = false;
                value?.IsHighlighted = true;
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    public string Description => Highlighted?.Description ?? "";

    /// <summary>"Connect from" with the node a dropped wire or a split connection joins, or null.</summary>
    public string? ConnectLabel { get; }

    public TaskNodeViewModel? ConnectNode { get; }

    public bool HasConnect => ConnectNode is not null;

    public ImmutableArray<string> Problems { get; }

    public bool HasProblems => !Problems.IsEmpty;

    public string ProblemsLabel => Problems.Length == 1 ? "1 library file did not read" : $"{Problems.Length} library files did not read";

    public string ProblemsDetail => string.Join("\n", Problems);

    /// <summary>Moves the highlight by <paramref name="step"/> rows, wrapping at either end.</summary>
    internal void Move(int step)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var index = Highlighted is { } row ? _rows.IndexOf(row) : -1;
        Highlighted = _rows[(((index + step) % _rows.Count) + _rows.Count) % _rows.Count];
    }

    /// <summary>Chooses the highlighted row.</summary>
    internal void Choose() => Highlighted?.Command.Execute(null);

    internal void Close() => _canvas.CloseAdd();

    /// <summary>
    /// How well <paramref name="query"/> matches: 0 for a name prefix, 1 for a word prefix, 2 for a substring of the name,
    /// 3 for letters of the name in order, 4 for a substring of the description, or null for no match.
    /// </summary>
    internal static int? Rank(string query, string name, string description)
    {
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        if (name.StartsWith(query, ignoreCase))
        {
            return 0;
        }

        if (name.Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries).Any(word => word.StartsWith(query, ignoreCase)))
        {
            return 1;
        }

        if (name.Contains(query, ignoreCase))
        {
            return 2;
        }

        var next = 0;
        foreach (var letter in name)
        {
            if (next < query.Length && char.ToUpperInvariant(letter) == char.ToUpperInvariant(query[next]))
            {
                next++;
            }
        }

        return next == query.Length ? 3 : description.Contains(query, ignoreCase) ? 4 : null;
    }

    private IEnumerable<AddNodeAction> Actions()
    {
        yield return new("Generate Workflow…", "IconSparkle", "", _canvas.OpenGenerateCommand);
        yield return new("Select All", "IconSelectAll", CanvasKeys.Hint(CanvasKeys.SelectAll), new RelayCommand(_canvas.SelectAll));
        yield return new("Fit to View", "IconFit", CanvasKeys.Hint(CanvasKeys.Fit), new RelayCommand(() => _canvas.View?.FitToView()));
        yield return new("Zoom to 100%", "IconZoomIn", CanvasKeys.Hint(CanvasKeys.ZoomToActual), new RelayCommand(() => _canvas.View?.ZoomToActual()));
        if (_canvas.SelectedNodes.Count > 0 || _canvas.SelectedConnections.Count > 0)
        {
            yield return new("Delete Selection", "IconDelete", CanvasKeys.Hint(CanvasKeys.Delete), _canvas.DeleteSelectionCommand);
        }
    }

    private static string Heading(string heading) => heading.Length == 0 ? heading : heading[..1] + heading[1..].ToLowerInvariant();

    private void Show()
    {
        var query = _query.Trim();
        if (query.Length == 0)
        {
            Items =
            [
                .. _libraries.SelectMany(library => library.Rows.Cast<AddNodeItem>().Prepend(new AddNodeHeader(library.Heading))),
                .. _actions.IsEmpty ? Enumerable.Empty<AddNodeItem>() : _actions.Cast<AddNodeItem>().Prepend(new AddNodeHeader("Actions")),
            ];
        }
        else
        {
            // OrderBy is stable, so rows that match equally keep the list's order.
            Items =
            [
                .. _libraries.SelectMany(library => library.Rows).Cast<AddNodeRow>().Concat(_actions)
                    .Select(row => (Row: row, Rank: Rank(query, row.Label, row.Description)))
                    .Where(match => match.Rank is not null)
                    .OrderBy(match => match.Rank)
                    .Select(match => match.Row),
            ];
        }

        _rows = [.. Items.OfType<AddNodeRow>()];
        Highlighted = _rows.FirstOrDefault();
    }

    private void Run(Action action)
    {
        Close();
        action();
    }

    private void Run(ICommand command) => Run(() => command.Execute(null));
}

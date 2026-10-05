using System.Collections.Immutable;
using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Blueprints;

/// <summary>One library's place in the palette: its heading and its blueprints, by name.</summary>
public sealed record PaletteGroup(string Heading, IReadOnlyList<BlueprintEntryViewModel> Entries)
{
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>
/// The palette in the right panel: the built-ins, the project library, and the personal library, and the editor that
/// derives, saves, and edits a library's blueprints. It reads the libraries when the project opens, after each of its
/// own saves, and when the person asks.
/// </summary>
public sealed class BlueprintsViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private IReadOnlyList<PaletteGroup> _groups = [];
    private ImmutableArray<string> _problems = [];
    private BlueprintEditorViewModel? _editor;

    internal BlueprintsViewModel(WorkflowCanvasViewModel canvas, BlueprintLibrary project, BlueprintLibrary? personal)
    {
        _canvas = canvas;
        Project = project;
        Personal = personal;
        ReloadCommand = new RelayCommand(Reload);
        Reload();
    }

    public IReadOnlyList<PaletteGroup> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    /// <summary>A message for each library file that did not read.</summary>
    public ImmutableArray<string> Problems
    {
        get => _problems;
        private set
        {
            _problems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => !Problems.IsEmpty;

    /// <summary>The blueprint being derived or edited, which the right panel shows in place of the inspector.</summary>
    public BlueprintEditorViewModel? Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    public ICommand ReloadCommand { get; }

    /// <summary>Every blueprint the palette offers to place, in its order, which a planner's type handles also list.</summary>
    internal IEnumerable<Blueprint> Placeable => Groups.SelectMany(group => group.Entries).Select(entry => entry.Blueprint);

    internal BlueprintLibrary Project { get; }

    internal BlueprintLibrary? Personal { get; }

    internal void Place(Blueprint blueprint) => _canvas.PlaceInView(blueprint);

    /// <summary>Opens the editor on a new blueprint with the structure and defaults of <paramref name="source"/>.</summary>
    internal void Derive(Blueprint source) => Editor = BlueprintEditorViewModel.Derive(this, source, $"{source.Name} copy", null, source.Defaults);

    /// <summary>Opens the editor on a new blueprint whose defaults are the node's field values and settings.</summary>
    internal void SaveAs(TaskDefinition node) => Editor = BlueprintEditorViewModel.Derive(
        this,
        node.Blueprint,
        string.IsNullOrWhiteSpace(node.Title) ? $"{node.Blueprint.Name} copy" : node.Title,
        node.Fields,
        new NodeSettings(node.Execution, node.Conversation));

    internal void Edit(Blueprint blueprint, BlueprintLibrary library) => Editor = BlueprintEditorViewModel.Edit(this, blueprint, library);

    internal void Close()
    {
        Editor = null;
        Reload();
    }

    internal void Notice(string? text) => _canvas.Notice(text);

    /// <summary>Reads both libraries again.</summary>
    internal void Reload()
    {
        var project = Project.Read();
        var personal = Personal?.Read() ?? new LibraryContents([], []);
        Blueprint[] libraries = [.. project.Blueprints, .. personal.Blueprints];
        NodeKind KindOf(Blueprint blueprint) => NodeKinds.Of(
            blueprint, key => _canvas.Workflow.Blueprints.GetValueOrDefault(key) ?? libraries.FirstOrDefault(library => library.Key == key));
        BlueprintEntryViewModel Entry(Blueprint blueprint, BlueprintLibrary? library) => new(this, blueprint, library, KindOf(blueprint));
        Groups =
        [
            new PaletteGroup("BUILT-IN", [.. BuiltInBlueprints.All.Select(blueprint => Entry(blueprint, null))]),
            new PaletteGroup("PROJECT", [.. project.Blueprints.Select(blueprint => Entry(blueprint, Project))]),
            .. Personal is { } library
                ? [new PaletteGroup("PERSONAL", [.. personal.Blueprints.Select(blueprint => Entry(blueprint, library))])]
                : Array.Empty<PaletteGroup>(),
        ];
        Problems = [.. project.Problems, .. personal.Problems];
    }
}

/// <summary>A blueprint in the palette. A built-in offers only Place and Derive.</summary>
public sealed class BlueprintEntryViewModel
{
    private readonly BlueprintLibrary? _library;

    internal BlueprintEntryViewModel(BlueprintsViewModel owner, Blueprint blueprint, BlueprintLibrary? library, NodeKind kind)
    {
        Blueprint = blueprint;
        _library = library;
        Kind = kind;
        PlaceCommand = new RelayCommand(() => owner.Place(blueprint));
        DeriveCommand = new RelayCommand(() => owner.Derive(blueprint));
        EditCommand = new RelayCommand(() => owner.Edit(blueprint, library!), () => library is not null);
    }

    public Blueprint Blueprint { get; }

    public NodeKind Kind { get; }

    /// <summary>The blueprint comes from a project or personal library, not from the built-ins.</summary>
    public bool IsLibrary => !NodeKinds.IsBuiltIn(Blueprint);

    public string Name => Blueprint.Name;

    public string Description => Blueprint.Description;

    public string Version => $"Version {Blueprint.Key.Version}";

    public bool CanEdit => _library is not null;

    public string PlaceName => $"Place {Name}";

    public string DeriveName => $"Derive from {Name}";

    public string EditName => $"Edit {Name}";

    public ICommand PlaceCommand { get; }

    public ICommand DeriveCommand { get; }

    public ICommand EditCommand { get; }
}

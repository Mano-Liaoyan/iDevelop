using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Blueprints;

/// <summary>An entry in one of the editor's pickers.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Edits a blueprint's name, description, access, fields, template, and default settings. A derived blueprint saves as
/// version 1 of a new id into the library the person chooses. An edit saves as the next version into its own library.
/// Whether the agent proposes graph edits stays as the source has it.
/// </summary>
public sealed class BlueprintEditorViewModel : ObservableObject
{
    private readonly BlueprintsViewModel _owner;
    private readonly Blueprint _source;
    private readonly BlueprintLibrary? _editing;
    private readonly RelayCommand _save;
    private BlueprintKey _newKey;
    private string _name;
    private string _description;
    private string _template;
    private bool _readOnly;
    private Option<ConversationMode> _conversation;
    private Option<ClientId?> _client;
    private string _model;
    private string _reasoning;
    private bool _inPersonal;
    private string? _problem;

    private BlueprintEditorViewModel(
        BlueprintsViewModel owner, Blueprint source, BlueprintLibrary? editing, string name, IReadOnlyDictionary<string, string>? values, NodeSettings defaults)
    {
        _owner = owner;
        _source = source;
        _editing = editing;
        _name = name;
        _newKey = BlueprintLibrary.NewKey(name);
        _description = source.Description;
        var work = Agent(source.Work);
        _template = work.Template.Text;
        _readOnly = work.Access == AgentAccess.ReadOnly;
        _conversation = ConversationChoices.First(choice => choice.Value == defaults.Conversation);
        _client = ClientChoices.First(choice => choice.Value == defaults.Execution?.Client);
        _model = defaults.Execution?.Model ?? "";
        _reasoning = defaults.Execution?.Reasoning ?? "";
        foreach (var field in source.Fields)
        {
            Fields.Add(new FieldRowViewModel(this, field, values?.GetValueOrDefault(field.Key) ?? field.Default));
        }

        Fields.CollectionChanged += (_, _) => Validate();
        _save = new RelayCommand(Save, () => Problem is null);
        AddFieldCommand = new RelayCommand(AddField);
        CancelCommand = new RelayCommand(owner.Close);
        Validate();
    }

    public string Heading => IsNew ? "NEW BLUEPRINT" : "EDIT BLUEPRINT";

    public string Caption => _editing is { } library
        ? $"Version {_source.Key.Version} in the {LibraryName(library.Kind)} library. Saving makes version {_source.Key.Version + 1}, and nodes placed before keep their version."
        : $"Derived from {_source.Name}, version {_source.Key.Version}. Saving makes version 1.";

    public bool IsNew => _editing is null;

    /// <summary>A new blueprint goes to the project library unless the person chooses the personal one.</summary>
    public bool CanChoosePersonal => IsNew && _owner.Personal is not null;

    public bool InProject
    {
        get => !_inPersonal;
        set => InPersonal = !value;
    }

    public bool InPersonal
    {
        get => _inPersonal;
        set
        {
            if (SetProperty(ref _inPersonal, value && CanChoosePersonal))
            {
                OnPropertyChanged(nameof(InProject));
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                _newKey = BlueprintLibrary.NewKey(value);
                Validate();
            }
        }
    }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public bool ReadOnly
    {
        get => _readOnly;
        set => Set(ref _readOnly, value);
    }

    public ObservableCollection<FieldRowViewModel> Fields { get; } = [];

    public string Template
    {
        get => _template;
        set => Set(ref _template, value);
    }

    public string TemplateHelp =>
        "{{title}} inserts the node's title, {{inputs}} what earlier nodes handed on, and {{key}} a field's value. " +
        "{{#key}}…{{/key}} keeps its text only when the value is not blank.";

    public IReadOnlyList<Option<ConversationMode>> ConversationChoices { get; } =
        [.. Enum.GetValues<ConversationMode>().Select(mode => new Option<ConversationMode>(mode, RunText.ConversationChoice(mode)))];

    public Option<ConversationMode> Conversation
    {
        get => _conversation;
        set => Set(ref _conversation, value);
    }

    public IReadOnlyList<Option<ClientId?>> ClientChoices { get; } =
        [new(null, "None"), .. Clients.All.Select(id => new Option<ClientId?>(id, Clients.Name(id)))];

    public Option<ClientId?> Client
    {
        get => _client;
        set
        {
            if (value is not null && SetProperty(ref _client, value))
            {
                OnPropertyChanged(nameof(HasClient));
                Validate();
            }
        }
    }

    public bool HasClient => Client.Value is not null;

    /// <summary>The client's own model id, or blank for the client's default.</summary>
    public string Model
    {
        get => _model;
        set => Set(ref _model, value);
    }

    public string Reasoning
    {
        get => _reasoning;
        set => Set(ref _reasoning, value);
    }

    /// <summary>Why the blueprint cannot be saved as it stands, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    public ICommand SaveCommand => _save;

    public ICommand CancelCommand { get; }

    public ICommand AddFieldCommand { get; }

    internal static BlueprintEditorViewModel Derive(
        BlueprintsViewModel owner, Blueprint source, string name, IReadOnlyDictionary<string, string>? values, NodeSettings defaults) =>
        new(owner, source, null, name, values, defaults);

    internal static BlueprintEditorViewModel Edit(BlueprintsViewModel owner, Blueprint blueprint, BlueprintLibrary library) =>
        new(owner, blueprint, library, blueprint.Name, null, blueprint.Defaults);

    internal void Remove(FieldRowViewModel field) => Fields.Remove(field);

    internal void Validate()
    {
        Problem = Build(out var problem) is null ? problem : null;
        _save.NotifyCanExecuteChanged();
    }

    private BlueprintKey Key => IsNew ? _newKey : _source.Key with { Version = _source.Key.Version + 1 };

    private BlueprintLibrary Library => _editing ?? (_inPersonal && _owner.Personal is { } personal ? personal : _owner.Project);

    /// <summary>The editor edits agent works, the only work so far. A new work fails the build here.</summary>
    private static WorkSpec.Agent Agent(WorkSpec work) => work.Kind switch
    {
        WorkKind.Agent => (WorkSpec.Agent)work,
    };

    private static string LibraryName(LibraryKind kind) => kind switch
    {
        LibraryKind.Project => "project",
        LibraryKind.Personal => "personal",
    };

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        Validate();
        return true;
    }

    private Blueprint? Build(out string? problem)
    {
        problem = null;
        PromptTemplate template;
        try
        {
            template = PromptTemplate.Parse(_template);
        }
        catch (FormatException e)
        {
            problem = $"The template: {e.Message}";
            return null;
        }

        var execution = _client.Value is { } client ? new ExecutionSettings(client) { Model = _model, Reasoning = _reasoning } : null;
        try
        {
            return new Blueprint(
                Key,
                _name,
                new WorkSpec.Agent(_readOnly ? AgentAccess.ReadOnly : AgentAccess.Edit, Agent(_source.Work).Proposes, template),
                [.. Fields.Select(field => field.Spec)],
                new NodeSettings(execution, _conversation.Value))
            {
                Description = _description.Trim(),
                DerivedFrom = IsNew ? _source.Key : _source.DerivedFrom,
            };
        }
        catch (BlueprintException e)
        {
            problem = e.Message;
            return null;
        }
    }

    private void Save()
    {
        if (Build(out var problem) is not { } blueprint)
        {
            Problem = problem;
            return;
        }

        var library = Library;
        try
        {
            library.Save(blueprint);
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Problem = e is ProjectException ? e.Message : $"Couldn't save {blueprint.Name}: {e.Message}";
            return;
        }

        _owner.Close();
        _owner.Notice($"Saved {blueprint.Name}, version {blueprint.Key.Version}, to the {LibraryName(library.Kind)} library.");
    }

    private void AddField()
    {
        var number = Enumerable.Range(1, Fields.Count + 1).First(n => Fields.All(field => field.Key != $"field{n}"));
        Fields.Add(new FieldRowViewModel(this, new FieldSpec($"field{number}", $"Field {number}", FieldShape.Line, Required: false, ""), ""));
    }
}

/// <summary>A field in the editor. Each change checks the blueprint again.</summary>
public sealed class FieldRowViewModel : ObservableObject
{
    private readonly BlueprintEditorViewModel _editor;
    private string _key;
    private string _label;
    private bool _multiline;
    private bool _required;
    private string _default;

    internal FieldRowViewModel(BlueprintEditorViewModel editor, FieldSpec field, string defaultText)
    {
        _editor = editor;
        _key = field.Key;
        _label = field.Label;
        _multiline = field.Shape == FieldShape.Text;
        _required = field.Required;
        _default = defaultText;
        RemoveCommand = new RelayCommand(() => editor.Remove(this));
    }

    /// <summary>The template variable that inserts the field's value.</summary>
    public string Key
    {
        get => _key;
        set => Set(ref _key, value);
    }

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool Multiline
    {
        get => _multiline;
        set => Set(ref _multiline, value);
    }

    public bool Required
    {
        get => _required;
        set => Set(ref _required, value);
    }

    /// <summary>The value a placed node starts with.</summary>
    public string Default
    {
        get => _default;
        set => Set(ref _default, value);
    }

    public ICommand RemoveCommand { get; }

    internal FieldSpec Spec => new(_key.Trim(), _label.Trim(), _multiline ? FieldShape.Text : FieldShape.Line, _required, _default);

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            _editor.Validate();
        }
    }
}

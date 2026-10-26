using System.Collections.Immutable;
using System.Windows.Input;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A node's skill picker (#93): the skills its client loads on this machine, each ticked while the node uses it. A node
/// starts with no choice, which uses every skill, including skills installed later, so every listed skill shows ticked.
/// The first untick keeps exactly the ticked skills, and the revert arrow goes back to no choice. The inspector's Agent
/// section shows it, and any other view of the node, such as an expanded card, can bind the same instance.
/// </summary>
public sealed class SkillPickerViewModel : ObservableObject
{
    private static readonly string[] Shown =
    [
        nameof(CanChoose), nameof(Summary), nameof(SummaryTip), nameof(ShowsList), nameof(Choices), nameof(ListNote),
        nameof(ListFooter), nameof(Note), nameof(CanRevert),
    ];

    private readonly TaskNodeViewModel _node;
    private bool _isOpen;

    internal SkillPickerViewModel(TaskNodeViewModel node)
    {
        _node = node;
        RevertCommand = new RelayCommand(() => Set(null));
    }

    /// <summary>Whether the node's client can limit its skills. Antigravity CLI cannot, so its picker stays off and says why.</summary>
    public bool CanChoose => Settings is { } settings && SkillChoices.CanChoose(settings.Client);

    /// <summary>The picker's list is open. Opening it lists the client's skills the first time.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                Refresh();
            }
        }
    }

    public bool ShowsList => IsOpen && CanChoose;

    /// <summary>"All skills" without a choice, else how many the node chose.</summary>
    public string Summary => Settings?.Skills switch
    {
        null => "All skills",
        { Count: 0 } => "No skills",
        { Count: 1 } => "1 skill",
        { Count: var count } => $"{count} skills",
    };

    /// <summary>The chosen skills' names, which the picker's tooltip lists.</summary>
    public string? SummaryTip => CanChoose && Settings?.Skills is { Count: > 0 } chosen ? string.Join(", ", chosen) : null;

    /// <summary>The listed skills, then each chosen skill that the client no longer lists, marked not installed.</summary>
    public IReadOnlyList<SkillChoiceViewModel> Choices => ShowsList && Listing is SkillListing.Listed listed
        ? [.. SkillChoices.Entries(Settings!.Client, Settings.Skills, listed.Skills).Select(entry => new SkillChoiceViewModel(this, entry))]
        : [];

    /// <summary>What the open list shows in place of skills: that it is listing them, why it cannot, or that there are none.</summary>
    public string? ListNote => !ShowsList ? null : Listing switch
    {
        SkillListing.Listed { Skills.IsEmpty: true } when Settings!.Skills is not { Count: > 0 } => $"{ClientName} loads no skills in this project.",
        SkillListing.Listed => null,
        SkillListing.Unavailable unavailable => $"iDevelop could not list the skills {ClientName} loads. {unavailable.Reason} {RunText.TryListingAgain}",
        _ => $"Listing the skills {ClientName} loads…",
    };

    /// <summary>
    /// The lines under the open list: what the listing leaves out, an older Claude Code's built-in skills or the project's
    /// own skills while the folder is not trusted in Pi, then what a choice leaves to the client anyway, as Claude Code's
    /// subagents (#93, owner decisions 2026-10-11).
    /// </summary>
    public string? ListFooter => !ShowsList || Listing is not SkillListing.Listed listed ? null
        : string.Join(" ", new[]
        {
            listed.LeavesOutBuiltIns ? $"{ClientName} doesn't list its built-in skills, so a choice leaves them out." : null,
            listed.ProjectNotTrusted ? $"This project's skills appear after the folder is trusted in {ClientName}. Check agents again once it is." : null,
            SkillChoices.Beyond(Settings!.Client),
        }.OfType<string>()) is { Length: > 0 } footer ? footer : null;

    /// <summary>
    /// The note under the picker: that Antigravity CLI uses all of its skills, the chosen skills that are not installed,
    /// which the task runs without, or that the client's skill list couldn't be read, and what the task gets instead.
    /// </summary>
    public string? Note => Settings is not { } settings ? null
        : !SkillChoices.CanChoose(settings.Client) ? $"{ClientName} uses all of its installed skills; it can't limit them per task."
        : Listing is SkillListing.Listed listed && SkillChoices.Missing(settings.Skills, listed.Skills) is { IsEmpty: false } missing ? MissingNote(missing)
        : settings.Skills is not null && Listing is SkillListing.Unavailable ? SkillChoices.UncheckedNote(settings.Client)
        : null;

    /// <summary>The node chose its skills, which the revert arrow clears, so the node uses every skill again.</summary>
    public bool CanRevert => CanChoose && Settings?.Skills is not null;

    public ICommand RevertCommand { get; }

    private ExecutionSettings? Settings => _node.Definition.Execution;

    private string ClientName => Settings is { } settings ? ClientCatalog.Name(settings.Client) : "";

    /// <summary>The client's listing for the project, asked for only while the list is open or the node has a choice to check.</summary>
    private SkillListing? Listing => Settings is { } settings && SkillChoices.CanChoose(settings.Client) && (IsOpen || settings.Skills is not null)
        ? _node.Canvas.Clients.Skills(settings.Client, _node.Canvas.Document.ProjectFolder)
        : null;

    /// <summary>The node's agent or what the clients list changed.</summary>
    internal void Refresh()
    {
        foreach (var property in Shown)
        {
            OnPropertyChanged(property);
        }
    }

    internal void Choose(string name, bool tick)
    {
        if (Settings is { } settings && CanChoose && Listing is SkillListing.Listed listed)
        {
            var chosen = SkillChoices.Toggle(settings.Skills, listed.Skills, name, tick);
            if (settings.Skills is null || !chosen.SetEquals(settings.Skills))
            {
                Set(chosen);
                return;
            }
        }

        Refresh();
    }

    private void Set(ImmutableSortedSet<string>? skills)
    {
        if (Settings is { } settings)
        {
            _node.SetSkills(settings with { Skills = skills });
        }
    }

    private static string MissingNote(ImmutableArray<string> missing) => missing.Length == 1
        ? $"{missing[0]} is not installed, so the task runs without it."
        : $"{string.Join(", ", missing.Take(missing.Length - 1))} and {missing[^1]} are not installed, so the task runs without them.";
}

/// <summary>One skill in a node's picker. A chosen skill the client no longer lists stays, so the person can see it and untick it.</summary>
public sealed class SkillChoiceViewModel(SkillPickerViewModel picker, SkillEntry entry)
{
    public string Name => entry.Name;

    public string Label => entry.Installed ? entry.Name : $"{entry.Name} (not installed)";

    /// <summary>The client's own description of the skill, which the tooltip shows, or null.</summary>
    public string? Description => string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description;

    public bool IsInstalled => entry.Installed;

    /// <summary>
    /// "Also lets in probeplug:probe-alpha." for a chosen name that Claude Code also matches in plugin skills, or null. It
    /// wraps only between names: a word joiner after each hyphen keeps a name on one line while it fits one.
    /// </summary>
    public string? LetsIn => entry.LetsIn.IsEmpty ? null : $"Also lets in {Join([.. entry.LetsIn.Select(Unbroken)])}.";

    private static string Unbroken(string name) => name.Replace("-", "-\u2060", StringComparison.Ordinal);

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    public bool IsChosen
    {
        get => entry.Chosen;
        set
        {
            if (value != entry.Chosen)
            {
                picker.Choose(entry.Name, value);
            }
        }
    }
}

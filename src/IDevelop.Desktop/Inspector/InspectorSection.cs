using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// A foldable group of inspector rows under a Title Case header, such as Agent. The window remembers each key's fold,
/// whichever node is selected. A folded section counts its changed values in its header, and while the filter has
/// text a section shows open, or hides its header when neither its title nor any of its rows match. Its content stays
/// in the layout then, because rows from a template exist only once laid out, and each row hides itself.
/// </summary>
public sealed class InspectorSection : InspectorPart
{
    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<InspectorSection, string>(nameof(Key), "");

    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<InspectorSection, string>(nameof(Title), "");

    /// <summary>The automation id of the title's text, for a title that tests and UI Automation read.</summary>
    public static readonly StyledProperty<string?> TitleIdProperty = AvaloniaProperty.Register<InspectorSection, string?>(nameof(TitleId));

    /// <summary>How many of the section's values differ from the blueprint's defaults.</summary>
    public static readonly StyledProperty<int> ChangesProperty = AvaloniaProperty.Register<InspectorSection, int>(nameof(Changes));

    public static readonly DirectProperty<InspectorSection, string> BadgeProperty =
        AvaloniaProperty.RegisterDirect<InspectorSection, string>(nameof(Badge), section => section.Badge);

    private readonly HashSet<InspectorRow> _rows = [];
    private Button? _header;
    private string _badge = "";

    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? TitleId
    {
        get => GetValue(TitleIdProperty);
        set => SetValue(TitleIdProperty, value);
    }

    public int Changes
    {
        get => GetValue(ChangesProperty);
        set => SetValue(ChangesProperty, value);
    }

    /// <summary>"(2)" while the section is folded with two changed values, otherwise empty.</summary>
    public string Badge
    {
        get => _badge;
        private set => SetAndRaise(BadgeProperty, ref _badge, value);
    }

    public bool IsFolded => !IsFiltering && State?.IsFolded(Key) == true;

    /// <summary>While the filter has text, whether it matches the title, which shows every row of the section.</summary>
    internal bool TitleMatches => !IsFiltering || InspectorFilter.Matches(State!.Query, Title);

    internal void Track(InspectorRow row)
    {
        _rows.Add(row);
        Refresh();
    }

    internal void Untrack(InspectorRow row)
    {
        _rows.Remove(row);
        Refresh();
    }

    internal override void Refresh()
    {
        var folded = IsFolded;
        PseudoClasses.Set(":folded", folded);
        Badge = folded && Changes > 0 ? $"({Changes})" : "";
        IsVisible = IsShown;
        PseudoClasses.Set(":unmatched", !TitleMatches && !_rows.Any(row => row.IsShown && row.LabelMatches));
        foreach (var row in _rows)
        {
            row.Refresh();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_header is not null)
        {
            _header.Click -= OnHeaderClick;
        }

        _header = e.NameScope.Find<Button>("PART_Header");
        if (_header is not null)
        {
            _header.Click += OnHeaderClick;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KeyProperty)
        {
            AutomationProperties.SetAutomationId(this, $"Section{Key}");
            Refresh();
        }
        else if (change.Property == TitleProperty || change.Property == ChangesProperty)
        {
            Refresh();
        }
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (State is { } state && !state.IsFiltering)
        {
            state.Fold([Key], !state.IsFolded(Key));
        }
    }
}

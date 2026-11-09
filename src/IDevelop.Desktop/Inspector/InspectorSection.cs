using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// A fold panel of a page, such as one task field, under a header with a chevron and a title. The window remembers each key's
/// fold, whichever node is selected. Left folds it and Right opens it from the header.
/// </summary>
public sealed class InspectorSection : InspectorPart
{
    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<InspectorSection, string>(nameof(Key), "");

    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<InspectorSection, string>(nameof(Title), "");

    /// <summary>The automation id of the title's text, for a title that tests and UI Automation read.</summary>
    public static readonly StyledProperty<string?> TitleIdProperty = AvaloniaProperty.Register<InspectorSection, string?>(nameof(TitleId));

    /// <summary>The panel starts folded until the person opens it.</summary>
    public static readonly StyledProperty<bool> StartsFoldedProperty = AvaloniaProperty.Register<InspectorSection, bool>(nameof(StartsFolded));

    /// <summary>A control at the header's right edge, such as Edit.</summary>
    public static readonly StyledProperty<object?> HeaderActionProperty = AvaloniaProperty.Register<InspectorSection, object?>(nameof(HeaderAction));

    private Button? _header;

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

    public bool StartsFolded
    {
        get => GetValue(StartsFoldedProperty);
        set => SetValue(StartsFoldedProperty, value);
    }

    public object? HeaderAction
    {
        get => GetValue(HeaderActionProperty);
        set => SetValue(HeaderActionProperty, value);
    }

    public bool IsFolded => State?.IsFolded(Key, StartsFolded) == true;

    internal override void Refresh()
    {
        PseudoClasses.Set(":folded", IsFolded);
        IsVisible = IsShown;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_header is not null)
        {
            _header.Click -= OnHeaderClick;
            _header.KeyDown -= OnHeaderKeyDown;
        }

        _header = e.NameScope.Find<Button>("PART_Header");
        if (_header is not null)
        {
            _header.Click += OnHeaderClick;
            _header.KeyDown += OnHeaderKeyDown;
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
        else if (change.Property == StartsFoldedProperty)
        {
            Refresh();
        }
    }

    private void OnHeaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right && e.KeyModifiers == KeyModifiers.None)
        {
            State?.Fold(Key, e.Key == Avalonia.Input.Key.Left);
            e.Handled = true;
        }
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e) => State?.Fold(Key, !IsFolded);
}

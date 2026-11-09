using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The Agent panel: a 56 px header, and under it a node's pages with their tab strip, or the page of what else is selected.
/// The blueprint editor takes the whole panel while it is open.
/// </summary>
public partial class AgentPanelView : UserControl
{
    /// <summary>The window's page state.</summary>
    public static readonly StyledProperty<AgentPanelViewModel?> PanelProperty = AvaloniaProperty.Register<AgentPanelView, AgentPanelViewModel?>(nameof(Panel));

    private readonly InspectorState _state = new();

    public AgentPanelView()
    {
        InitializeComponent();
        InspectorState.SetCurrent(this, _state);
        InspectorState.SetCurrent(EditorHost, new InspectorState());
        AddHandler(KeyDownEvent, OnKeyDownInPanel, RoutingStrategies.Bubble);
        EditorHost.PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentProperty)
            {
                EditorHost.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToHome();
            }
        };
    }

    public AgentPanelViewModel? Panel
    {
        get => GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    /// <summary>Shows the Agent page, focuses the client picker, and opens its list, as the person would.</summary>
    internal void FocusClientPicker()
    {
        Panel?.Show(AgentPage.Agent);
        UpdateLayout();
        if (Control("TaskClient") is not ComboBox picker)
        {
            return;
        }

        // A list opens against where its picker was last laid out, so the picker scrolls into view and is laid out first.
        // A hidden picker's list cannot open, so it opens only once the picker has the focus.
        picker.BringIntoView();
        UpdateLayout();
        if (picker.Focus(NavigationMethod.Tab))
        {
            picker.IsDropDownOpen = true;
        }
    }

    private Control? Control(string automationId) =>
        this.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);

    private void OnAgentChosen(object? sender, SelectionChangedEventArgs e) => AgentPicker.OnChosen(sender);

    private void OnConversationChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: ConversationChoice choice } picker && AgentPicker.IsChoice(picker))
        {
            task.ChooseConversation(choice);
        }
    }

    private void RenameTitle()
    {
        if (Control("TaskTitle") is TextBox title)
        {
            title.Focus(NavigationMethod.Tab);
            title.SelectAll();
        }
    }

    private void OnKeyDownInPanel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None && Control("TaskTitle") is not null)
        {
            RenameTitle();
            e.Handled = true;
        }
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e) => RenameTitle();

    private void OnRenameTitle(object? sender, RoutedEventArgs e) => RenameTitle();

    // Enter ends the rename, so the header reads as a title again.
    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnClosePanel(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.ViewModel.ToggleInspectorCommand.Execute(null);
}

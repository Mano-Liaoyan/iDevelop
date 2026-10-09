using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Proto;

// THROWAWAY N1 PROTOTYPE. Never merge.
public partial class CardDetail : UserControl
{
    /// <summary>Names a section of the body, so a tabbed option can show one at a time.</summary>
    public static readonly AttachedProperty<string?> SectionProperty =
        AvaloniaProperty.RegisterAttached<CardDetail, Control, string?>("Section");

    /// <summary>The one section a tabbed option shows, or null to show every section.</summary>
    public static readonly StyledProperty<string?> ShownSectionProperty =
        AvaloniaProperty.Register<CardDetail, string?>(nameof(ShownSection));

    /// <summary>Shows the actions under the body. Option C pins them under its tabs instead.</summary>
    public static readonly StyledProperty<bool> ShowsActionsProperty =
        AvaloniaProperty.Register<CardDetail, bool>(nameof(ShowsActions), true);

    public bool ShowsActions
    {
        get => GetValue(ShowsActionsProperty);
        set => SetValue(ShowsActionsProperty, value);
    }

    public CardDetail()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static string? GetSection(Control control) => control.GetValue(SectionProperty);

    public static void SetSection(Control control, string? value) => control.SetValue(SectionProperty, value);

    public string? ShownSection
    {
        get => GetValue(ShownSectionProperty);
        set => SetValue(ShownSectionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShownSectionProperty)
        {
            ShowSections();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowSections();
    }

    // A tabbed option shows one section and no hairlines; the others stack every section.
    private void ShowSections()
    {
        if (Content is not StackPanel root)
        {
            return;
        }

        foreach (var child in root.Children)
        {
            if (child is Border { Classes: var classes } && classes.Contains("hairline") && child != root.Children[^2])
            {
                child.Opacity = ShownSection is null ? 1 : 0;
                child.Height = ShownSection is null ? 1 : 0;
                continue;
            }

            if (GetSection(child) is { } section && section != "actions")
            {
                // A hidden tab's section takes no room, whatever its own visibility binding says.
                child.Classes.Set("tabHidden", ShownSection is not null && ShownSection != section);
            }
        }
    }

    private TaskNodeViewModel? Node => DataContext as TaskNodeViewModel;

    private void OnClientChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is [ClientChoice choice] && Node is { } node)
        {
            node.ChooseClient(choice);
        }
    }

    private void OnModelChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is [Choice choice] && Node is { } node)
        {
            node.ChooseModel(choice);
        }
    }

    private void OnReasoningChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is [Choice choice] && Node is { } node)
        {
            node.ChooseReasoning(choice);
        }
    }
}

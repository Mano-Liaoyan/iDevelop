using System.Collections.Immutable;
using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The canvas's side of expanded cards (#94, ADR 0024): Expand All and Collapse All for this workflow's cards, the layout
/// that slides cards aside on screen while others are expanded, and the expanded cards the window remembers for the next
/// start. None of it is an edit, so none of it marks the workflow unsaved.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>An expanded card's container: the 320 px card and its two 10 px port gutters.</summary>
    public const double ExpandedCardWidth = 340;

    private RelayCommand? _expandCards;
    private bool _layoutScheduled;
    private bool _expandingAll;

    /// <summary>Some card of the workflow is expanded, so the canvas offers Collapse All.</summary>
    public bool AnyCardExpanded => Nodes.Any(node => node.IsExpanded);

    /// <summary>"Expand All", or "Collapse All" while any card is expanded.</summary>
    public string ExpandCardsLabel => AnyCardExpanded ? "Collapse All" : "Expand All";

    /// <summary>Expands every card of this workflow, or collapses every card while any is expanded.</summary>
    public ICommand ExpandCardsCommand => _expandCards ??= new RelayCommand(() =>
    {
        var expand = !AnyCardExpanded;
        // One change for all the cards, so the window remembers them once.
        _expandingAll = true;
        try
        {
            foreach (var node in Nodes)
            {
                node.IsExpanded = expand;
            }
        }
        finally
        {
            _expandingAll = false;
        }

        OnCardExpansionChanged();
    });

    /// <summary>The expanded cards, which the window remembers for this workflow on this machine.</summary>
    public ImmutableArray<TaskId> ExpandedCards => [.. Nodes.Where(node => node.IsExpanded).Select(node => node.Id)];

    /// <summary>Expands the cards the last session left expanded. A task that no longer exists is skipped.</summary>
    internal void RestoreExpandedCards(IEnumerable<TaskId> tasks)
    {
        foreach (var id in tasks)
        {
            if (_nodes.TryGetValue(id, out var node))
            {
                node.IsExpanded = true;
            }
        }
    }

    internal void OnCardExpansionChanged()
    {
        if (_expandingAll)
        {
            return;
        }

        OnPropertyChanged(nameof(AnyCardExpanded));
        OnPropertyChanged(nameof(ExpandCardsLabel));
        OnPropertyChanged(nameof(ExpandedCards));
    }

    /// <summary>
    /// Lays the cards out again once the current batch of changes is done: a card's size, a move, or a card that arrives or
    /// leaves. One layout follows many such changes.
    /// </summary>
    internal void ScheduleCardLayout()
    {
        if (_layoutScheduled)
        {
            return;
        }

        _layoutScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _layoutScheduled = false;
            LayOutCards();
        }, DispatcherPriority.Render);
    }

    private void LayOutCards()
    {
        var nodes = Nodes.ToList();
        var offsets = CardLayout.Offsets([.. nodes.Select(node => new CardBox(node.Location, node.CardSize))]);
        for (var index = 0; index < nodes.Count; index++)
        {
            nodes[index].ViewOffset = offsets[index];
        }

        // A card that grew stands in other wires' way even where no card moved, and an open proposal's ghosts sit beside
        // the cards where they show.
        Reroute();
        if (Ghosts.OfType<GhostCardViewModel>().Any())
        {
            ShowGhosts();
        }
    }
}

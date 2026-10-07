using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using IDevelop.Execution;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// Shows one conversation and keeps the person's place in it. At the end it follows new output; anywhere else the entry at
/// the top of the view stays put through streaming, refreshes, and earlier pages, and Jump to latest goes back to the end.
/// </summary>
public partial class ConversationView : UserControl
{
    // Within this many pixels of the end, the view counts as at the end and follows new output.
    private const double EndSlack = 24;

    private ConversationViewModel? _model;
    private bool _follow = true;
    private bool _restoring;
    private bool _userInput;
    private double? _scrolledTo;
    private (EntryId Entry, double Offset)? _anchor;

    public ConversationView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += OnScrollChanged;
        // A wheel, a drag, the scroll bar, or a key scrolls on the person's behalf, even when rows coming into view change the
        // extent in the same scroll. Layout alone, such as an item measured for the first time, never decides whether the
        // view follows the end.
        Scroller.AddHandler(PointerWheelChangedEvent, (_, _) => _userInput = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.TemplateApplied += (_, e) =>
        {
            foreach (var bar in new[] { "PART_VerticalScrollBar", "PART_HorizontalScrollBar" }.Select(e.NameScope.Find<ScrollBar>).OfType<ScrollBar>())
            {
                bar.Scroll += (_, _) => _userInput = true;
            }
        };
        Scroller.AddHandler(PointerMovedEvent, (_, e) => _userInput |= e.GetCurrentPoint(Scroller).Properties.IsLeftButtonPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(KeyDownEvent, (_, _) => _userInput = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Transcript.LayoutUpdated += OnTranscriptLaidOut;
    }

    /// <summary>The entry at the top of the view and how far it scrolled into it, or null at the end.</summary>
    internal (EntryId Entry, double Offset)? Anchor => _follow ? null : _anchor;

    internal bool IsFollowing => _follow;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Items.CollectionChanged -= OnItemsChanged;
            _model.RevealRequested -= Reveal;
            _model.FollowRequested -= Follow;
        }

        _model = DataContext as ConversationViewModel;
        if (_model is not null)
        {
            _model.Items.CollectionChanged += OnItemsChanged;
            _model.RevealRequested += Reveal;
            _model.FollowRequested += Follow;
            _anchor = _model.State.Anchor;
            _follow = _anchor is null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Background);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Settle();

    private void OnTranscriptLaidOut(object? sender, EventArgs e) => Settle();

    // A new row, a longer message, or an earlier page moves what the person reads, so after each layout the view puts the
    // end or the anchored entry back where it was.
    private void Settle()
    {
        if (_restoring)
        {
            return;
        }

        _restoring = true;
        try
        {
            if (_follow)
            {
                ScrollTo(Scroller.Extent.Height - Scroller.Viewport.Height);
            }
            else if (_anchor is { } anchor)
            {
                if (Top(anchor.Entry) is { } top)
                {
                    var wanted = top + anchor.Offset;
                    ScrollTo(wanted);
                }
                else if (_model?.Items.Select(item => item.Id).ToList().IndexOf(anchor.Entry) is var index and >= 0)
                {
                    Transcript.ScrollIntoView(index.Value);
                }
                else if (_model is { Items.Count: > 0, Idle.IsCompleted: true })
                {
                    _follow = true;
                    _anchor = null;
                    ScrollTo(Scroller.Extent.Height - Scroller.Viewport.Height);
                }
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ScrollTo(double y)
    {
        var wanted = Math.Max(0, y);
        if (Math.Abs(Scroller.Offset.Y - wanted) > 0.5)
        {
            _scrolledTo = wanted;
            Scroller.Offset = Scroller.Offset.WithY(wanted);
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var atEnd = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - EndSlack;
        JumpToLatest.IsVisible = !atEnd && _model is { Items.Count: > 0 };
        var ours = _scrolledTo is { } target && Math.Abs(Scroller.Offset.Y - target) < 1;
        var layoutOnly = (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) && !_userInput;
        _userInput = false;
        if (_restoring || ours || layoutOnly)
        {
            return;
        }

        _scrolledTo = null;

        _follow = atEnd;
        _anchor = _follow ? null : TopEntry();
        if (_model is not null)
        {
            _model.State.Anchor = _anchor;
        }
    }

    /// <summary>The first entry whose bottom lies below the top of the view, and how far the view's top is into it.</summary>
    private (EntryId Entry, double Offset)? TopEntry()
    {
        var viewTop = Scroller.Offset.Y;
        foreach (var container in Transcript.GetRealizedContainers().OrderBy(container => Top(container) ?? double.MaxValue))
        {
            if (container.DataContext is ConversationItemViewModel item && Top(container) is { } top && top + container.Bounds.Height > viewTop)
            {
                return (item.Id, viewTop - top);
            }
        }

        return null;
    }

    private double? Top(EntryId entry) =>
        Transcript.GetRealizedContainers().FirstOrDefault(container => container.DataContext is ConversationItemViewModel item && item.Id == entry) is { } found
            ? Top(found)
            : null;

    // Positions are in the scroll viewer's content, whose top is offset zero.
    private double? Top(Control container) => container.TranslatePoint(default, (Visual)Scroller.Content!)?.Y;

    private void Reveal(EntryId entry)
    {
        if (_model?.Items.Select(item => item.Id).ToList().IndexOf(entry) is not (var index and >= 0))
        {
            return;
        }

        _follow = false;
        _anchor = (entry, -16);
        Transcript.ScrollIntoView(index);
        Dispatcher.UIThread.Post(Settle, DispatcherPriority.Loaded);
    }

    private void Follow()
    {
        _follow = true;
        _anchor = null;
        if (_model is not null)
        {
            _model.State.Anchor = null;
        }

        Settle();
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e) => Follow();
}

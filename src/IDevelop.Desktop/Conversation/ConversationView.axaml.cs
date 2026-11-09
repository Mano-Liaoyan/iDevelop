using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// Shows one conversation and keeps the person's place in it. At the end it follows new output; anywhere else the entry at
/// the top of the view stays put through streaming, refreshes, and earlier pages, and Jump to latest goes back to the end.
/// </summary>
public partial class ConversationView : UserControl
{
    /// <summary>The column of a message: a person's message is in the second column.</summary>
    public static readonly IValueConverter MessageColumn = new FuncValueConverter<bool, int>(isPerson => isPerson ? 1 : 0);

    /// <summary>The columns a message spans: a person's message spans one, and other messages span both.</summary>
    public static readonly IValueConverter MessageColumnSpan = new FuncValueConverter<bool, int>(isPerson => isPerson ? 1 : 2);

    /// <summary>The margin of a transcript row: the side gutter and the row's gap above.</summary>
    public static readonly IValueConverter RowMargin = new FuncValueConverter<double, Thickness>(gap => new Thickness(16, gap, 16, 0));

    // Text softens at the view's top edge, so a block under the header reads as scrolled, and a whole line resting there stays whole. A mask reads only
    // each stop's alpha, so these stops are opacities and no part of the palette.
    private static readonly LinearGradientBrush TopFade = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(0, 4, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(default, 0), new GradientStop(new Color(255, 0, 0, 0), 1) },
    };

    // Within this many pixels of the end, the view counts as at the end and follows new output.
    private const double EndSlack = 24;

    // The composer's hint needs this much room for the composer's tool row.
    private const double HintWidth = 340;

    /// <summary>The least transcript the Chat page keeps in a short window.</summary>
    internal const double MinTranscriptHeight = 180;

    // The dock's share of the page, and the least it keeps for its heading and a line while the page is short.
    private const double DockShare = 0.4;
    private const double MinDockHeight = 72;

    private ConversationViewModel? _model;
    private bool _follow = true;
    private bool _restoring;
    private bool _userInput;
    private bool _settleQueued;
    private double? _scrolledTo;
    private (int Entry, double Offset)? _anchor;
    private bool _pressed;
    private bool _restQueued;
    private bool _movedDown;

    // The extent where the view last moved above the end to rest on a whole line, and the extent where such a move changed the end.
    private double? _restedAt;
    private double? _restMovedEnd;

    public ConversationView()
    {
        InitializeComponent();
        Scroller.OpacityMask = TopFade;
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
        Scroller.AddHandler(PointerPressedEvent, (_, _) => _pressed = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _pressed = false;
            QueueRest();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Transcript.LayoutUpdated += OnTranscriptLaidOut;
        Transcript.AddHandler(KeyDownEvent, OnStepKey, RoutingStrategies.Bubble);
        Composer.AddHandler(KeyDownEvent, OnComposerKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnEscape, RoutingStrategies.Bubble);
        ComposerBar.SizeChanged += (_, e) => KeyHint.IsVisible = e.NewSize.Width >= HintWidth;
        ComposerFrame.SizeChanged += (_, _) => LimitDock();
        SizeChanged += (_, _) => LimitDock();
    }

    // The dock takes at most 40% of the page, and the transcript keeps its least height after the dock and the composer. The dock's own content scrolls.
    private void LimitDock()
    {
        var composer = ComposerFrame.IsVisible ? ComposerFrame.Bounds.Height + ComposerFrame.Margin.Top + ComposerFrame.Margin.Bottom : 0;
        DockView.MaxHeight = Math.Max(MinDockHeight, Math.Min(Bounds.Height * DockShare, Bounds.Height - composer - DockView.Margin.Top - DockView.Margin.Bottom - MinTranscriptHeight));
    }

    /// <summary>The entry at the top of the view and how far it scrolled into it, or null at the end.</summary>
    internal (int Entry, double Offset)? Anchor => _follow ? null : _anchor;

    internal bool IsFollowing => _follow;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Items.CollectionChanged -= OnItemsChanged;
            _model.FollowRequested -= Follow;
        }

        _model = DataContext as ConversationViewModel;
        if (_model is not null)
        {
            _model.Items.CollectionChanged += OnItemsChanged;
            _model.FollowRequested += Follow;
            _anchor = _model.State.Anchor;
            _follow = _anchor is null;
        }
    }

    /// <summary>Puts the keys in the composer, or in the transcript while no message can go.</summary>
    internal void FocusComposer()
    {
        if (Composer.IsEffectivelyVisible && Composer.IsEffectivelyEnabled)
        {
            Composer.Focus(NavigationMethod.Tab);
        }
        else
        {
            Scroller.Focus(NavigationMethod.Tab);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Settle();
        // A page of rows of many heights can move the anchored row in the last layout pass, after the pass's own settling.
        if (!_settleQueued)
        {
            _settleQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _settleQueued = false;
                Settle();
            }, DispatcherPriority.Background);
        }
    }

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
                RestAtEnd();
            }
            else if (_anchor is { } anchor)
            {
                if (Top(anchor.Entry) is { } top)
                {
                    // A scroll in progress rests on its own, in the way it went.
                    ScrollTo(_pressed || _restQueued ? top + anchor.Offset : OnWholeLine(top + anchor.Offset));
                }
                else if (_model?.Items.Select(item => item.Id).ToList().IndexOf(anchor.Entry) is var index and >= 0)
                {
                    Transcript.ScrollIntoView(index.Value);
                }
                else if (_model is { Items.Count: > 0 })
                {
                    _follow = true;
                    _anchor = null;
                    RestAtEnd();
                }
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private double End => Scroller.Extent.Height - Scroller.Viewport.Height;

    // The list estimates a row that left the view, and a row the dock holds has no height, so a small move up can change the
    // extent and the end with it. A move to that new end brings the old extent back, and the two would repeat until the layout
    // aborts. An extent where this happened keeps the end itself.
    private void RestAtEnd()
    {
        var extent = Scroller.Extent.Height;
        if (_restedAt is { } before && Math.Abs(before - extent) >= 0.5)
        {
            _restMovedEnd = before;
            _restedAt = null;
        }

        var end = End;
        var wanted = _restMovedEnd is { } moved && Math.Abs(moved - extent) < 0.5 ? end : OnWholeLine(end);
        if (wanted < end && Math.Abs(Scroller.Offset.Y - wanted) > 0.5)
        {
            _restedAt = extent;
        }

        ScrollTo(wanted);
    }

    // The height at or above y where the view's top edge cuts no line. The space under the last row takes up the difference at the end.
    private double OnWholeLine(double y) => Scroller.Content is Control content ? WholeLine.Rest(content, y, down: false) : y;

    private void QueueRest()
    {
        if (!_restQueued)
        {
            _restQueued = true;
            Dispatcher.UIThread.Post(Rest, DispatcherPriority.Background);
        }
    }

    // A scroll that stops in a line moves on past it, in the way it went, so a small step still gets somewhere. A drag rests when it ends.
    private void Rest()
    {
        _restQueued = false;
        if (_pressed || _follow || Scroller.Content is not Control content)
        {
            return;
        }

        var wanted = Math.Min(WholeLine.Rest(content, Scroller.Offset.Y, _movedDown), End);
        ScrollTo(OnWholeLine(wanted));
        _anchor = TopEntry();
        if (_model is not null)
        {
            _model.State.Anchor = _anchor;
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

        if (e.OffsetDelta.Y != 0)
        {
            _movedDown = e.OffsetDelta.Y > 0;
            QueueRest();
        }
    }

    /// <summary>The first entry whose bottom lies below the top of the view, and how far the view's top is into it.</summary>
    private (int Entry, double Offset)? TopEntry()
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

    private double? Top(int entry) =>
        Transcript.GetRealizedContainers().FirstOrDefault(container => container.DataContext is ConversationItemViewModel item && item.Id == entry) is { } found
            ? Top(found)
            : null;

    // Positions are in the scroll viewer's content, whose top is offset zero.
    private double? Top(Control container) => container.TranslatePoint(default, (Visual)Scroller.Content!)?.Y;

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

    // Enter sends and Ctrl+Enter also sends. Shift+Enter is the box's new line. A digit picks a choice while the composer is empty. The tunnel comes before the box, which would take the key.
    private void OnComposerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control or KeyModifiers.Meta)
        {
            if (_model?.Node.SendCommand is { } send && send.CanExecute(null))
            {
                send.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers is (KeyModifiers.Control | KeyModifiers.Shift) or (KeyModifiers.Meta | KeyModifiers.Shift))
        {
            if (_model?.Node.StopAndSendCommand is { } stopAndSend && stopAndSend.CanExecute(null))
            {
                stopAndSend.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9
            && _model?.PickChoice(e.Key >= Key.NumPad1 ? e.Key - Key.NumPad0 : e.Key - Key.D0) == true)
        {
            e.Handled = true;
            Composer.CaretIndex = Composer.Text?.Length ?? 0;
        }
    }

    // Esc moves one distance back: from the Chat page to the node's card on the Map.
    private void OnEscape(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.FocusMap();
            e.Handled = true;
        }
    }

    // Left and Right fold and open a group of steps, as they do a tree's row.
    private static void OnStepKey(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.Left or Key.Right, KeyModifiers: KeyModifiers.None, Source: ToggleButton { DataContext: StepGroupItemViewModel group } })
        {
            group.IsOpen = e.Key == Key.Right;
            e.Handled = true;
        }
    }

    private void OnCloseModel(object? sender, RoutedEventArgs e) => ModelChip.Flyout?.Hide();

    private void OnAgentChosen(object? sender, SelectionChangedEventArgs e) => AgentPicker.OnChosen(sender);

    private void OnShowResult(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.ViewModel.AgentPanel.Show(Inspector.AgentPage.Output);

    private void OnRowEntered(object? sender, PointerEventArgs e) => PlaceCopy(sender as Panel);

    private void OnCopyFocused(object? sender, GotFocusEventArgs e) => PlaceCopy((sender as Control)?.Parent as Panel);

    // Copy sits at the end of an agent's last line, next to the text, or beside a person's bubble. A line with no room left puts it under the line.
    private static void PlaceCopy(Panel? row)
    {
        var copy = row?.Children.OfType<Button>().FirstOrDefault(button => button.Classes.Contains("copy"));
        var message = row?.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("message"));
        if (row is null || copy is null || message is null)
        {
            return;
        }

        var size = copy.Width + 4;
        Point at;
        if (copy.Classes.Contains("person"))
        {
            at = message.TranslatePoint(new Point(-size, message.Bounds.Height - copy.Height), row) ?? default;
        }
        else if (message.GetVisualDescendants().OfType<TextBlock>().LastOrDefault(text => text.IsEffectivelyVisible && text.TextLayout.TextLines.Count > 0) is { } text)
        {
            var last = text.TextLayout.TextLines[^1];
            var top = text.TextLayout.TextLines.SkipLast(1).Sum(line => line.Height);
            var end = text.TranslatePoint(new Point(last.Start + last.WidthIncludingTrailingWhitespace + text.Padding.Left, top + text.Padding.Top), row) ?? default;
            at = end.X + size > row.Bounds.Width
                ? new Point(row.Bounds.Width - copy.Width, end.Y + last.Height)
                : new Point(end.X + 4, end.Y + (last.Height - copy.Height) / 2);
        }
        else
        {
            return;
        }

        copy.Margin = new Thickness(Math.Max(0, at.X), Math.Max(0, at.Y), 0, 0);
    }
}

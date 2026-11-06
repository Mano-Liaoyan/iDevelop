using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// Shows one conversation and keeps the person's place in it. At the end it follows new output; anywhere else the entry at
/// the top of the view stays put through streaming, refreshes, and earlier pages, and Jump to latest goes back to the end.
/// </summary>
public partial class ConversationView : UserControl
{
    /// <summary>The column of a message: a person's message is in the second column, next to its copy button.</summary>
    public static readonly IValueConverter MessageColumn = new FuncValueConverter<bool, int>(isPerson => isPerson ? 1 : 0);

    /// <summary>The columns a message spans: a person's message spans one, and other messages span both.</summary>
    public static readonly IValueConverter MessageColumnSpan = new FuncValueConverter<bool, int>(isPerson => isPerson ? 1 : 2);

    /// <summary>The column of Copy: beside a person's message, and over the end of other messages.</summary>
    public static readonly IValueConverter CopyColumn = new FuncValueConverter<bool, int>(isPerson => isPerson ? 0 : 1);

    /// <summary>The attention card's row of its actions: under its text, or on its header's line.</summary>
    public static readonly IValueConverter ActionsRow = new FuncValueConverter<bool, int>(compact => compact ? 0 : 2);

    public static readonly IValueConverter ActionsColumn = new FuncValueConverter<bool, int>(compact => compact ? 1 : 0);

    public static readonly IValueConverter ActionsSpan = new FuncValueConverter<bool, int>(compact => compact ? 1 : 2);

    // Under this width the transcript's and the composer's side padding shrinks.
    private const double NarrowWidth = 600;

    // Within this many pixels of the end, the view counts as at the end and follows new output.
    private const double EndSlack = 24;

    private ConversationViewModel? _model;
    private bool _follow = true;
    private bool _restoring;
    private bool _userInput;
    private bool _settleQueued;
    private double? _scrolledTo;
    private (int Entry, double Offset)? _anchor;

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
        Transcript.AddHandler(KeyDownEvent, OnStepKey, RoutingStrategies.Bubble);
        Composer.AddHandler(KeyDownEvent, OnComposerDigit, RoutingStrategies.Tunnel);
        Strip.SizeChanged += (_, e) => _model?.FitChips(e.NewSize.Width - Strip.Padding.Left - Strip.Padding.Right);
        SizeChanged += (_, e) =>
        {
            Rows.Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
            LimitCard();
        };
    }

    // The row of Rows that holds the transcript.
    private const int TranscriptRow = 2;

    /// <summary>The least transcript a docked conversation keeps: a few lines of messages.</summary>
    internal const double MinTranscriptHeight = 96;

    // What the view keeps for the attention card in a short dock, and its largest share of the view.
    private const double MinCardHeight = 96;
    private const double MinCompactCardHeight = 36;
    private const double CardShare = 0.45;

    private double _aroundCard;

    /// <summary>Raised, after a layout, when <see cref="RequiredHeight"/> changed.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> RequiredHeightChangedEvent =
        RoutedEvent.Register<ConversationView, RoutedEventArgs>("RequiredHeightChanged", RoutingStrategies.Bubble);

    /// <summary>
    /// The height the view needs: its header, the strip, the status line, and the composer as tall as they lay out at
    /// its width, what the attention card keeps while its own content scrolls, and <see cref="MinTranscriptHeight"/> of transcript.
    /// </summary>
    internal double RequiredHeight { get; private set; }

    // The grid gives each row but the transcript's the height it asks for, however short the view, so their sum is what the
    // view needs. The window learns of a change after the layout, where it may resize the dock.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        var card = AttentionCard.IsVisible ? AttentionCard.Bounds.Height + AttentionCard.Margin.Top + AttentionCard.Margin.Bottom : 0;
        _aroundCard = Rows.RowDefinitions.Where((_, index) => index != TranscriptRow).Sum(row => row.ActualHeight) - card;
        var required = _aroundCard + Math.Min(card, LeastCard) + MinTranscriptHeight;
        if (Math.Abs(required - RequiredHeight) >= 0.5)
        {
            RequiredHeight = required;
            Dispatcher.UIThread.Post(() => RaiseEvent(new RoutedEventArgs(RequiredHeightChangedEvent)));
        }

        return size;
    }

    // A compact card keeps its action on its header's line, so the header alone is enough in a short dock.
    private double LeastCard => _model?.IsCompactCard == true ? MinCompactCardHeight : MinCardHeight;

    // The attention card takes at most 45% of the view and always leaves the transcript its least height. Its own content scrolls.
    private void LimitCard() => AttentionCard.MaxHeight = Math.Max(LeastCard, Math.Min(Bounds.Height * CardShare, Bounds.Height - _aroundCard - MinTranscriptHeight));

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

    /// <summary>Puts the keys in the transcript, where Esc and the arrows still work and the composer waits for a click.</summary>
    internal void FocusTranscript() => Scroller.Focus();

    /// <summary>Puts the keys in the composer.</summary>
    internal void FocusComposer() => Composer.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The composer shows the keys are there, so it takes them only while the node waits for the person's words.
        Dispatcher.UIThread.Post(() =>
        {
            if (_model?.WaitsForWords == true)
            {
                Composer.Focus();
            }
            else
            {
                FocusTranscript();
            }
        }, DispatcherPriority.Background);
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
                else if (_model is { Items.Count: > 0 })
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

    // A digit picks a choice while the composer is empty. The tunnel comes before the box, which would type the digit.
    private void OnComposerDigit(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9
            && _model?.PickChoice(e.Key >= Key.NumPad1 ? e.Key - Key.NumPad0 : e.Key - Key.D0) == true)
        {
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

    private void OnOpenSendBack(object? sender, RoutedEventArgs e)
    {
        if (_model is { } model)
        {
            model.IsSendBackOpen = !model.IsSendBackOpen;
            if (model.IsSendBackOpen)
            {
                Dispatcher.UIThread.Post(() => SendBackNote.Focus(), DispatcherPriority.Background);
            }
        }
    }

    private void OnChangeModel(object? sender, RoutedEventArgs e) => ModelChip.Flyout?.ShowAt(ModelChip);

    private void OnAgentChosen(object? sender, SelectionChangedEventArgs e) => AgentPicker.OnChosen(sender);

    private void OnShowResult(object? sender, RoutedEventArgs e)
    {
        if (_model?.Node.SelectCommand is { } select && select.CanExecute(null))
        {
            select.Execute(null);
        }

        (TopLevel.GetTopLevel(this) as MainWindow)?.ViewModel.ShowInspector();
    }
}

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Conversation;

/// <summary>A conversation to open: the task in its workflow, and the request to show, if any.</summary>
public sealed record ConversationTarget(WorkflowCanvasViewModel Canvas, TaskId Task, RequestKey? Request = null);

/// <summary>An entry in the attempt picker. Its label follows the attempt's status in place.</summary>
public sealed class AttemptChoice(AttemptId id, string label) : ObservableObject
{
    private string _label = label;

    public AttemptId Id { get; } = id;

    public string Label
    {
        get => _label;
        internal set => SetProperty(ref _label, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// One task's conversation as the view shows it: the selected attempt's history in pages, the composer, and the answers.
/// It reads through one <see cref="IConversationSession"/> and keeps nothing the person typed; the task's
/// <see cref="ConversationState"/> does. Closing it never stops the task.
/// </summary>
public sealed class ConversationViewModel : ObservableObject, IDisposable
{
    internal const int PageSize = 50;

    // Each refresh reads every loaded window again, so the windows that pages from the tail add are folded back into one.
    private const int MaxWindows = 4;

    private const int MaxAnchorPages = 20;

    private readonly IConversationSession _session;
    private readonly Func<string, Task> _copy;
    private readonly List<LoadedPage> _windows = [];
    private readonly RelayCommand _send;
    private readonly RelayCommand _stopAndSend;
    private readonly RelayCommand _cancel;
    private readonly RelayCommand _markDone;
    private readonly RelayCommand _openInTerminal;
    private readonly RelayCommand _loadEarlier;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskNodeViewModel? _node;
    private ConversationSnapshot _snapshot;
    private ImmutableArray<AttemptSummary> _attempts = [];
    private AttemptId? _head;
    private long _logRevision = -1;
    private int _generation;
    private bool _dirty;
    private bool _earlier;
    private bool _opened;
    private int _posted;
    private RequestKey? _seek;
    private bool _busy;
    private string? _notice;
    private Task _sync = Task.CompletedTask;

    internal ConversationViewModel(ConversationTarget target, ConversationState state, IConversationSession session, Func<string, Task> copy)
    {
        Target = target;
        State = state;
        _session = session;
        _copy = copy;
        _snapshot = session.Snapshot;
        _node = target.Canvas.Nodes.FirstOrDefault(node => node.Id == target.Task);
        _send = new RelayCommand(() => _ = SendAsync(stopTurn: false), () => CanSend);
        _stopAndSend = new RelayCommand(() => _ = SendAsync(stopTurn: true), () => CanSend && ShowsStopAndSend);
        _cancel = new RelayCommand(() => _ = RunAsync(turn => _session.CancelAsync(turn, CancellationToken.None)), () => ShowsCancel && !_busy);
        _markDone = new RelayCommand(() => _ = RunAsync(turn => _session.MarkDoneAsync(turn, CancellationToken.None)), () => ShowsMarkDone && !_busy);
        _openInTerminal = new RelayCommand(() => _ = OpenInTerminalAsync(), () => ShowsTerminal && !_busy);
        _loadEarlier = new RelayCommand(() =>
        {
            _earlier = true;
            Invalidate();
        }, () => HasEarlier);
        ReturnToCurrentCommand = new RelayCommand(() => SelectedAttempt = Attempts.LastOrDefault());
        state.PropertyChanged += OnStateChanged;
        if (_node is not null)
        {
            _node.PropertyChanged += OnNodeChanged;
        }

        session.Changed += OnSessionChanged;
        Invalidate();
    }

    public ConversationTarget Target { get; }

    internal ConversationState State { get; }

    public string Title => _node?.Title ?? _snapshot.Latest?.TaskTitle ?? "Task";

    /// <summary>The project and workflow the task belongs to, as the breadcrumb names them.</summary>
    public string Breadcrumb => $"{Target.Canvas.Project.Name} › {Target.Canvas.Name}";

    public string AgentName => Client is { } client ? Clients.Name(client) : "The agent";

    // Before its first attempt, a task's conversation names the agent the task is set to run.
    private ClientId? Client => _snapshot.Latest?.Requested.Client ?? _node?.SelectedClient.Id;

    public string StatusLabel => RunText.StatusLabel(_snapshot.Latest, elsewhere: false);

    /// <summary>What the client cannot do here, such as answering questions only as text.</summary>
    public string Limitations => _snapshot.Capabilities.Limitations;

    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];

    public ObservableCollection<AttemptChoice> Attempts { get; } = [];

    /// <summary>The attempt whose history shows. Choosing the latest follows new attempts again.</summary>
    public AttemptChoice? SelectedAttempt
    {
        get => Attempts.FirstOrDefault(choice => choice.Id == (_head ?? State.SelectedAttempt ?? _snapshot.Latest?.Id));
        set
        {
            if (value is null)
            {
                return;
            }

            var selected = value.Id == _snapshot.Latest?.Id ? (AttemptId?)null : value.Id;
            if (selected == State.SelectedAttempt && value.Id == _head)
            {
                return;
            }

            State.SelectedAttempt = selected;
            State.Anchor = null;
            Restart();
        }
    }

    /// <summary>The person reads an earlier attempt, so nothing they write can go to it.</summary>
    public bool IsHistorical => _head is { } head && head != _snapshot.Latest?.Id;

    public string Draft
    {
        get => State.Draft;
        set => State.Draft = value;
    }

    public string ComposerHint => IsHistorical ? "Return to current conversation to send."
        : !_snapshot.Actions.Send.Enabled ? _snapshot.Actions.Send.Reason
        : _snapshot.Latest switch
        {
            { Subject: not null } => "Your guidance reaches both agents in their next message.",
            { Status: AttemptStatus.Running } => "Your message waits for this turn to end. Stop and send ends the turn first.",
            { Status: AttemptStatus.WaitingForInput } => "Your reply continues this attempt.",
            _ => "Sending continues the session in a new attempt.",
        };

    public string Watermark => _snapshot.Latest?.Subject is not null ? "Guide the review"
        : Client is { } client ? $"Message {Clients.Name(client)}"
        : "Message the agent";

    /// <summary>Why the last command did not go through, or null.</summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public bool HasEarlier => _windows.Count > 0 && _windows[0].HasEarlier;

    public bool ShowsStopAndSend => !IsHistorical && _snapshot.Actions.StopAndSend.Enabled;

    public bool ShowsCancel => !IsHistorical && _snapshot.Actions.Cancel.Enabled;

    public bool ShowsMarkDone => !IsHistorical && _snapshot.Actions.MarkDone.Enabled;

    public bool ShowsTerminal => !IsHistorical && _snapshot.Actions.Terminal.Enabled;

    public ICommand SendCommand => _send;

    public ICommand StopAndSendCommand => _stopAndSend;

    public ICommand CancelCommand => _cancel;

    public ICommand MarkDoneCommand => _markDone;

    public ICommand OpenInTerminalCommand => _openInTerminal;

    public ICommand LoadEarlierCommand => _loadEarlier;

    public ICommand ReturnToCurrentCommand { get; }

    /// <summary>The view scrolls to this entry, such as a request that card attention opened.</summary>
    internal event Action<EntryId>? RevealRequested;

    /// <summary>The view scrolls to the latest entry and follows new ones, such as after the person sends.</summary>
    internal event Action? FollowRequested;

    /// <summary>Completes when the reads that the latest change started have finished.</summary>
    internal Task Idle => _sync;

    private bool CanSend => !IsHistorical && !_busy && _snapshot.Actions.Send.Enabled && !string.IsNullOrWhiteSpace(Draft) && _snapshot.Current is not null;

    public void Dispose()
    {
        _generation++;
        _closed.Cancel();
        _session.Changed -= OnSessionChanged;
        State.PropertyChanged -= OnStateChanged;
        if (_node is not null)
        {
            _node.PropertyChanged -= OnNodeChanged;
        }

        _session.Dispose();
    }

    internal Task CopyAsync(string text) => _copy(text);

    internal async Task<AnswerResult> AnswerAsync(RequestKey key, QuestionsReply reply)
    {
        var result = await _session.AnswerAsync(key, reply, CancellationToken.None);
        Invalidate();
        return result;
    }

    /// <summary>Shows the request at its place in the history, in its own attempt when the selected one does not hold it.</summary>
    internal Task SeekAsync(RequestKey request)
    {
        _seek = request;
        Invalidate();
        return _sync;
    }

    // Changes can arrive for every line a client writes, so one pending read stands for all that came before it.
    private void OnSessionChanged(long revision)
    {
        if (Interlocked.Exchange(ref _posted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _posted, 0);
                Invalidate();
            }, DispatcherPriority.Background);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationState.Draft))
        {
            OnPropertyChanged(nameof(Draft));
            NotifyCommands();
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.Title))
        {
            OnPropertyChanged(nameof(Title));
        }
        else if (e.PropertyName == nameof(TaskNodeViewModel.SelectedClient))
        {
            OnPropertyChanged(nameof(AgentName));
            OnPropertyChanged(nameof(Watermark));
        }
    }

    private void Restart()
    {
        _generation++;
        _head = null;
        _windows.Clear();
        Invalidate();
    }

    /// <summary>Reads again what changed. Changes during a read run one more read after it, never two at once.</summary>
    private void Invalidate()
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }

        _dirty = true;
        if (_sync.IsCompleted)
        {
            _sync = SyncLoop();
        }
    }

    // The one loop that changes the loaded pages, so a refresh, a seek, and an earlier page never interleave.
    private async Task SyncLoop()
    {
        while ((_dirty || _seek is not null || _earlier) && !_closed.IsCancellationRequested)
        {
            try
            {
                if (_dirty)
                {
                    _dirty = false;
                    await SyncAsync(_generation);
                }

                if (_seek is { } request)
                {
                    _seek = null;
                    await SeekOnceAsync(request, _generation);
                }

                if (_earlier)
                {
                    _earlier = false;
                    await LoadEarlierAsync(_generation);
                }
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested)
            {
            }
        }
    }

    private async Task SeekOnceAsync(RequestKey request, int generation)
    {
        var head = _head ?? _snapshot.Latest?.Id;
        if (head is null)
        {
            return;
        }

        var page = await _session.ReadPageAsync(head.Value, new HistoryQuery.AroundRequest(request), PageSize, _closed.Token);
        if (page is HistoryResult.Unavailable && head != request.Turn.Attempt)
        {
            head = request.Turn.Attempt;
            page = await _session.ReadPageAsync(head.Value, new HistoryQuery.AroundRequest(request), PageSize, _closed.Token);
        }

        if (generation != _generation || page is not HistoryResult.Page found)
        {
            return;
        }

        State.SelectedAttempt = head == _snapshot.Latest?.Id ? null : head;
        _head = head;
        _windows.Clear();
        _windows.Add(new LoadedPage(found));
        NotifyHead();
        await ShowAsync(generation);
        if (generation == _generation && found.Entries.FirstOrDefault(entry => entry.Content is ConversationContent.Request r && r.Key == request) is { } entry)
        {
            RevealRequested?.Invoke(entry.Id);
        }
    }

    private async Task SyncAsync(int generation)
    {
        var snapshot = _session.Snapshot;
        var logChanged = snapshot.LogRevision != _logRevision || _attempts.IsEmpty;
        if (logChanged)
        {
            var attempts = await _session.ListAttemptsAsync(_closed.Token);
            if (generation != _generation)
            {
                return;
            }

            _attempts = attempts;
        }

        _logRevision = snapshot.LogRevision;
        ShowSnapshot(snapshot);
        var head = State.SelectedAttempt is { } selected && _attempts.Any(attempt => attempt.Id == selected) ? selected : snapshot.Latest?.Id;
        if (head is null)
        {
            _head = null;
            _windows.Clear();
            await ShowAsync(generation);
            return;
        }

        if (head != _head || _windows.Count == 0)
        {
            await LoadLatestAsync(head.Value, PageSize, generation);
            return;
        }

        for (var i = logChanged ? 0 : _windows.Count - 1; i < _windows.Count; i++)
        {
            var refreshed = await _session.ReadPageAsync(head.Value, new HistoryQuery.RefreshWindow(_windows[i].Token), PageSize, _closed.Token);
            if (generation != _generation)
            {
                return;
            }

            if (refreshed is not HistoryResult.Page page)
            {
                await LoadLatestAsync(head.Value, Math.Max(PageSize, Items.Count), generation);
                return;
            }

            _windows[i] = new LoadedPage(page);
        }

        while (true)
        {
            var later = await _session.ReadPageAsync(head.Value, new HistoryQuery.After(_windows[^1].Page.After), PageSize, _closed.Token);
            if (generation != _generation)
            {
                return;
            }

            if (later is not HistoryResult.Page page)
            {
                await LoadLatestAsync(head.Value, Math.Max(PageSize, Items.Count), generation);
                return;
            }

            if (page.Entries.IsEmpty)
            {
                break;
            }

            _windows.Add(new LoadedPage(page));
            if (!page.HasLater)
            {
                break;
            }
        }

        if (_windows.Count > MaxWindows)
        {
            await LoadLatestAsync(head.Value, _windows.Sum(window => window.Page.Entries.Length), generation);
            return;
        }

        await ShowAsync(generation);
    }

    private async Task LoadLatestAsync(AttemptId head, int count, int generation)
    {
        var latest = await _session.ReadPageAsync(head, new HistoryQuery.Latest(), count, _closed.Token);
        if (generation != _generation)
        {
            return;
        }

        _head = head;
        _windows.Clear();
        if (latest is HistoryResult.Page page)
        {
            _windows.Add(new LoadedPage(page));
        }
        else if (latest is HistoryResult.Unavailable unavailable)
        {
            Notice = unavailable.Reason;
        }

        // On opening, earlier pages load until the entry the person last read is back, so the view can return to it.
        if (!_opened && State.Anchor is { } anchor)
        {
            for (var pages = 0; pages < MaxAnchorPages && _windows.Count > 0 && _windows[0].HasEarlier
                && !_windows.Any(window => window.Page.Entries.Any(entry => entry.Id == anchor.Entry)); pages++)
            {
                var earlier = await _session.ReadPageAsync(head, new HistoryQuery.Before(_windows[0].Page.Before), PageSize, _closed.Token);
                if (generation != _generation || earlier is not HistoryResult.Page found)
                {
                    break;
                }

                _windows.Insert(0, new LoadedPage(found));
            }
        }

        _opened = true;
        NotifyHead();
        await ShowAsync(generation);
    }

    private async Task LoadEarlierAsync(int generation)
    {
        if (_head is not { } head || !HasEarlier)
        {
            return;
        }

        var earlier = await _session.ReadPageAsync(head, new HistoryQuery.Before(_windows[0].Page.Before), PageSize, _closed.Token);
        if (generation != _generation || earlier is not HistoryResult.Page page)
        {
            return;
        }

        _windows.Insert(0, new LoadedPage(page));
        await ShowAsync(generation);
    }

    /// <summary>Puts the loaded entries in the transcript, matched by id, then shows each request's latest fold.</summary>
    private async Task ShowAsync(int generation)
    {
        var entries = _windows.SelectMany(window => window.Page.Entries).DistinctBy(entry => entry.Id).ToList();
        var shown = entries.Select(entry => entry.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!shown.Contains(Items[i].Id))
            {
                Items.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i < Items.Count && Items[i].Id == entry.Id)
            {
                if (!Items[i].Update(entry))
                {
                    Items[i] = ConversationItemViewModel.Create(entry, this);
                }

                continue;
            }

            var at = IndexOf(entry.Id, i + 1);
            if (at >= 0)
            {
                Items.Move(at, i);
                if (!Items[i].Update(entry))
                {
                    Items[i] = ConversationItemViewModel.Create(entry, this);
                }
            }
            else
            {
                Items.Insert(i, ConversationItemViewModel.Create(entry, this));
            }
        }

        OnPropertyChanged(nameof(HasEarlier));
        _loadEarlier.NotifyCanExecuteChanged();
        foreach (var request in Items.OfType<RequestItemViewModel>().ToList())
        {
            var latest = _snapshot.Latest;
            var record = latest?.Id == request.Key.Turn.Attempt ? latest.Requests.GetValueOrDefault(request.Key) : request.Record;
            record ??= await _session.ReadRequestAsync(request.Key, _closed.Token);
            if (generation != _generation)
            {
                return;
            }

            request.Show(record, current: !IsHistorical && _snapshot.Current == request.Key.Turn);
            if (record is RequestRecord.Question { State: QuestionState.Closed { RecordedReply: null } } closed)
            {
                State.TransferDeferred(closed.Key, closed.Questions);
            }
        }
    }

    private int IndexOf(EntryId id, int from)
    {
        for (var i = from; i < Items.Count; i++)
        {
            if (Items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void ShowSnapshot(ConversationSnapshot snapshot)
    {
        _snapshot = snapshot;
        // The picker shows its selection only while the selected entry stays in its list, so an attempt keeps its entry
        // and a new status changes only the entry's label.
        for (var i = 0; i < _attempts.Length; i++)
        {
            var label = Label(_attempts[i], i + 1);
            if (i == Attempts.Count)
            {
                Attempts.Add(new AttemptChoice(_attempts[i].Id, label));
            }
            else if (Attempts[i].Id == _attempts[i].Id)
            {
                Attempts[i].Label = label;
            }
            else
            {
                Attempts[i] = new AttemptChoice(_attempts[i].Id, label);
            }
        }

        while (Attempts.Count > _attempts.Length)
        {
            Attempts.RemoveAt(Attempts.Count - 1);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AgentName));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Limitations));
        OnPropertyChanged(nameof(Watermark));
        NotifyHead();
    }

    /// <summary>Everything that depends on which attempt shows, after the snapshot or the shown attempt changed.</summary>
    private void NotifyHead()
    {
        OnPropertyChanged(nameof(SelectedAttempt));
        OnPropertyChanged(nameof(IsHistorical));
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(ShowsStopAndSend));
        OnPropertyChanged(nameof(ShowsCancel));
        OnPropertyChanged(nameof(ShowsMarkDone));
        OnPropertyChanged(nameof(ShowsTerminal));
        NotifyCommands();
    }

    private string Label(AttemptSummary attempt, int number)
    {
        var continues = attempt.Continues is { } earlier && _attempts.Select(item => item.Id).ToList().IndexOf(earlier) is var index and >= 0 ? $", continues {index + 1}" : "";
        var latest = attempt.Id == _snapshot.Latest?.Id ? " (current)" : "";
        return $"Attempt {number}{latest} · {attempt.Status.Describe()} · {Clients.Name(attempt.Settings.Client)}{continues}";
    }

    private void NotifyCommands()
    {
        _send.NotifyCanExecuteChanged();
        _stopAndSend.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
        _markDone.NotifyCanExecuteChanged();
        _openInTerminal.NotifyCanExecuteChanged();
    }

    // Commands take no cancellation: once sent, a message, an answer, or a cancel goes through even if the view closes.
    private async Task SendAsync(bool stopTurn)
    {
        var text = Draft;
        var turn = _snapshot.Current!.Value;
        _busy = true;
        NotifyCommands();
        try
        {
            var result = await _session.SendAsync(turn, text, stopTurn, CancellationToken.None);
            if (result is SendResult.Refused refused)
            {
                Notice = RunText.Describe(refused.Problem);
                return;
            }

            if (Draft == text)
            {
                Draft = "";
            }

            Notice = null;
            State.SelectedAttempt = null;
            FollowRequested?.Invoke();
        }
        finally
        {
            _busy = false;
            NotifyCommands();
            Invalidate();
        }
    }

    private async Task RunAsync(Func<TurnKey, Task<ConversationCommandResult>> command)
    {
        if (_snapshot.Current is not { } turn)
        {
            return;
        }

        _busy = true;
        NotifyCommands();
        try
        {
            var result = await command(turn);
            Notice = result.Outcome == CommandOutcome.Applied ? null : result.Detail;
        }
        finally
        {
            _busy = false;
            NotifyCommands();
            Invalidate();
        }
    }

    private async Task OpenInTerminalAsync()
    {
        if (_snapshot.Current is not { } turn)
        {
            return;
        }

        switch (await _session.OpenInTerminalAsync(turn, CancellationToken.None))
        {
            case TerminalResult.HandedOff handedOff:
                try
                {
                    await _copy(handedOff.Command);
                    Notice = RunText.HandedOff(handedOff);
                }
                catch (Exception)
                {
                    Notice = RunText.NotCopied(handedOff);
                }

                break;
            case TerminalResult.Refused refused:
                Notice = RunText.Describe(refused.Problem);
                break;
        }

        Invalidate();
    }

    /// <summary>One loaded page of history, which a refresh reads again by its window.</summary>
    private sealed record LoadedPage(HistoryResult.Page Page)
    {
        public HistoryWindow Token => Page.Window;

        public bool HasEarlier => Page.HasEarlier;
    }
}

internal static class AttemptStatusText
{
    public static string Describe(this AttemptStatus status) => status switch
    {
        AttemptStatus.Running => "Running",
        AttemptStatus.Succeeded => "Succeeded",
        AttemptStatus.Failed => "Failed",
        AttemptStatus.Cancelled => "Cancelled",
        AttemptStatus.Interrupted => "Interrupted",
        AttemptStatus.WaitingForInput => "Waiting for you",
        AttemptStatus.InReview => "In review",
    };
}

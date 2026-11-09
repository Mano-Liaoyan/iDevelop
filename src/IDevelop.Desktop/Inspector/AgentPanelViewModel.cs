using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Inspector;

/// <summary>The Agent panel's pages, in the order of their tabs and of Alt+1 to Alt+6.</summary>
public enum AgentPage
{
    Chat,
    Agent,
    Task,
    Input,
    Output,

    /// <summary>The conditional page: a review's Findings or a planner's Proposal.</summary>
    Extra,
}

/// <summary>
/// Which page the window's Agent panel shows, and at which size. Chat is first and the default. Selecting another node keeps the current page. Coming from
/// the Workflow page or a connection, the panel returns to the page last used for that kind of node. A page that the node does not have falls back to Chat.
/// A panel at <see cref="PanelWidths.PinFrom"/> or wider pins Chat beside the current page, so the page column then shows the last page other than Chat.
/// The Room is the panel at the size of the Map and the panel together.
/// </summary>
public sealed class AgentPanelViewModel : ObservableObject
{
    private readonly Dictionary<NodeKind, AgentPage> _lastPage = [];
    private TaskNodeViewModel? _node;

    // The last page other than Chat, which the page column shows beside a pinned Chat.
    private AgentPage _sidePage = AgentPage.Agent;

    public AgentPanelViewModel() => ShowCommand = new RelayCommand<AgentPage>(Show);

    public AgentPage Page
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                if (value != AgentPage.Chat)
                {
                    _sidePage = value;
                }

                OnPropertyChanged(nameof(IsChat));
                RaisePages();
            }
        }
    } = AgentPage.Chat;

    /// <summary>Chat is the current page. While Chat is pinned it can be, and the page column still shows another page.</summary>
    public bool IsChat => Page == AgentPage.Chat;

    /// <summary>The page that the panel's page area shows: the current page, or beside a pinned Chat a page other than Chat.</summary>
    public AgentPage ShownPage => !IsPinned || Page != AgentPage.Chat ? Page : _node is { } node && !Has(node, _sidePage) ? FirstSidePage(node) : _sidePage;

    public bool IsAgent => ShownPage == AgentPage.Agent;

    public bool IsTask => ShownPage == AgentPage.Task;

    public bool IsInput => ShownPage == AgentPage.Input;

    public bool IsOutput => ShownPage == AgentPage.Output;

    public bool IsExtra => ShownPage == AgentPage.Extra;

    /// <summary>The Chat column shows: its page is current, or it is pinned.</summary>
    public bool ShowsChat => IsChat || IsPinned;

    /// <summary>Chat stands beside the page column. The strip then has no Chat tab.</summary>
    public bool IsPinned => Width >= PanelWidths.PinFrom && !(IsRoom && IsCompactWindow);

    /// <summary>The Agent panel's width, which the window reports when the panel is laid out.</summary>
    internal double Width
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePages();
            }
        }
    }

    /// <summary>The panel fills the Map and panel area, under a Lineage Strip.</summary>
    public bool IsRoom
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(RoomTip));
                RaisePages();
            }
        }
    }

    /// <summary>The window is in the Compact tier, where a Room shows one column.</summary>
    internal bool IsCompactWindow
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePages();
            }
        }
    }

    /// <summary>What the header's Room button does.</summary>
    public string RoomTip => IsRoom ? "Back to the panel (Esc)" : "Open the Room (Shift+Enter)";

    /// <summary>The Chat page's conversation, which the window opens while the page shows, or null.</summary>
    public Conversation.ConversationViewModel? Conversation
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    public ICommand ShowCommand { get; }

    /// <summary>Whether the node has the page. Only an agent has the Agent page, and only a review or a proposal has the conditional page.</summary>
    public static bool Has(TaskNodeViewModel node, AgentPage page) => page switch
    {
        AgentPage.Agent => node.HasAgent,
        AgentPage.Extra => node.HasExtraPage,
        _ => true,
    };

    /// <summary>Shows the page, if the selected node has it, and remembers it for that kind of node.</summary>
    public void Show(AgentPage page)
    {
        if (_node is { } node && Has(node, page))
        {
            Page = page;
            _lastPage[node.Kind] = page;
        }
    }

    /// <summary>Follows the selection. A null node is the Workflow page, a connection, or several nodes.</summary>
    public void Follow(TaskNodeViewModel? node)
    {
        if (ReferenceEquals(node, _node))
        {
            return;
        }

        var fromNode = _node is not null;
        Watch(node);
        if (node is null)
        {
            return;
        }

        RaisePages();

        var wanted = !fromNode && _lastPage.TryGetValue(node.Kind, out var last) ? last : Page;
        if (Has(node, wanted))
        {
            Page = wanted;
            _lastPage[node.Kind] = wanted;
        }
        else
        {
            Page = FirstPage(node);
        }
    }

    private static AgentPage FirstPage(TaskNodeViewModel node) => AgentPage.Chat;

    private static AgentPage FirstSidePage(TaskNodeViewModel node) => node.HasAgent ? AgentPage.Agent : AgentPage.Task;

    private void RaisePages()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(ShowsChat));
        OnPropertyChanged(nameof(ShownPage));
        OnPropertyChanged(nameof(IsAgent));
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsInput));
        OnPropertyChanged(nameof(IsOutput));
        OnPropertyChanged(nameof(IsExtra));
    }

    private void Watch(TaskNodeViewModel? node)
    {
        if (_node is not null)
        {
            _node.PropertyChanged -= OnNodeChanged;
        }

        _node = node;
        if (node is not null)
        {
            node.PropertyChanged += OnNodeChanged;
        }
    }

    // A proposal that closes takes its page with it.
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.HasExtraPage) && _node is { } node)
        {
            if (!Has(node, Page))
            {
                Page = FirstPage(node);
            }

            RaisePages();
        }
    }
}

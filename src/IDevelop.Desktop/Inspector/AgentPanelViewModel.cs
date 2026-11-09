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
/// Which page the window's Agent panel shows. Selecting another node keeps the current page. Coming from the Workflow page
/// or a connection, the panel returns to the page last used for that kind of node. A page that the node does not have falls
/// back to the node's first page.
/// </summary>
public sealed class AgentPanelViewModel : ObservableObject
{
    private readonly Dictionary<NodeKind, AgentPage> _lastPage = [];
    private TaskNodeViewModel? _node;

    public AgentPanelViewModel() => ShowCommand = new RelayCommand<AgentPage>(Show);

    public AgentPage Page
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsChat));
                OnPropertyChanged(nameof(IsAgent));
                OnPropertyChanged(nameof(IsTask));
                OnPropertyChanged(nameof(IsInput));
                OnPropertyChanged(nameof(IsOutput));
                OnPropertyChanged(nameof(IsExtra));
            }
        }
    } = AgentPage.Agent;

    public bool IsChat => Page == AgentPage.Chat;

    public bool IsAgent => Page == AgentPage.Agent;

    public bool IsTask => Page == AgentPage.Task;

    public bool IsInput => Page == AgentPage.Input;

    public bool IsOutput => Page == AgentPage.Output;

    public bool IsExtra => Page == AgentPage.Extra;

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

    private static AgentPage FirstPage(TaskNodeViewModel node) => node.HasAgent ? AgentPage.Agent : AgentPage.Task;

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
        if (e.PropertyName == nameof(TaskNodeViewModel.HasExtraPage) && _node is { } node && !Has(node, Page))
        {
            Page = FirstPage(node);
        }
    }
}

using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>A blueprint that Replace With offers.</summary>
public sealed record ReplaceChoice(string Name, NodeKind Kind, bool IsLibrary, ICommand Command)
{
    public string AutomationName => $"Replace with {Name}";
}

/// <summary>
/// The node menu's subject: the selection when the menu opened. The menu hides what the selection cannot do rather than
/// dimming it.
/// </summary>
public sealed class NodeMenuViewModel
{
    internal NodeMenuViewModel(WorkflowCanvasViewModel canvas)
    {
        TaskId[] selection = [.. canvas.SelectedNodes.Select(node => node.Id)];
        Node = canvas.SelectedNodes is [var single] ? single : null;
        ShowRun = Node is { HasAgent: true } node && node.RunCommand.CanExecute(null);
        ShowCancel = Node?.CancelCommand.CanExecute(null) == true;
        ShowReplace = Node is { } one && !canvas.HasStarted(one.Id);
        ShowDisconnect = canvas.Workflow.Connections.Keys.Any(key => selection.Contains(key.From) || selection.Contains(key.To));
        ReplaceChoices = Node is { } replaced && ShowReplace
            ? [.. canvas.Blueprints.Placeable
                .Select(blueprint => new ReplaceChoice(
                    blueprint.Name, canvas.KindOf(blueprint), !blueprint.IsBuiltIn, new RelayCommand(() => canvas.Replace(replaced, blueprint))))]
            : [];
        RenameCommand = new RelayCommand(() => Node?.BeginRename());
        DuplicateCommand = new RelayCommand(canvas.Duplicate);
        DisconnectCommand = new RelayCommand(() => canvas.Disconnect(selection));
        DeleteCommand = canvas.DeleteSelectionCommand;
    }

    /// <summary>The selected node when exactly one is selected, else null.</summary>
    public TaskNodeViewModel? Node { get; }

    public bool IsSingle => Node is not null;

    public bool ShowRun { get; }

    public bool ShowCancel { get; }

    public bool ShowRunGroup => ShowRun || ShowCancel;

    /// <summary>Only a node that never started can change its blueprint.</summary>
    public bool ShowReplace { get; }

    public bool ShowDisconnect { get; }

    public IReadOnlyList<ReplaceChoice> ReplaceChoices { get; }

    public ICommand RenameCommand { get; }

    public ICommand DuplicateCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand DeleteCommand { get; }
}

/// <summary>The connection menu's subject: the connection and the canvas point it was right-clicked at.</summary>
public sealed class ConnectionMenuViewModel
{
    internal ConnectionMenuViewModel(WorkflowCanvasViewModel canvas, ConnectionViewModel connection, CanvasPoint point)
    {
        Connection = connection;
        DependencyCommand = new RelayCommand(() => SetKind(ConnectionKind.Dependency));
        ContextCommand = new RelayCommand(() => SetKind(ConnectionKind.Context));
        InsertCommand = new RelayCommand(() => canvas.OpenAdd(new AddTarget.Between(connection.Key, point)));
    }

    public ConnectionViewModel Connection { get; }

    public bool IsDependency => Connection.Kind == ConnectionKind.Dependency;

    public bool IsContext => Connection.Kind == ConnectionKind.Context;

    /// <summary>Choosing the current kind, which carries the checkmark, changes nothing.</summary>
    public ICommand DependencyCommand { get; }

    public ICommand ContextCommand { get; }

    public ICommand InsertCommand { get; }

    private void SetKind(ConnectionKind kind)
    {
        if (Connection.SetKindCommand.CanExecute(kind))
        {
            Connection.SetKindCommand.Execute(kind);
        }
    }
}

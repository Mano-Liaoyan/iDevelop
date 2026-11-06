using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the node needs from the person, and the one way the card and the inspector open its conversation.</summary>
public sealed partial class TaskNodeViewModel
{
    private RelayCommand _openConversation = null!;

    /// <summary>A wait for the person, or a failure. Opening the conversation leaves it.</summary>
    public Attention? Attention => Attention.Of(_engineState);

    /// <summary>The name of the node's agent client, or null while it has none.</summary>
    public string? AgentClientName => Definition.Execution?.Client is { } client ? IDevelop.Clients.ClientCatalog.Name(client) : null;

    /// <summary>The files a commit of this node changed.</summary>
    internal System.Collections.Immutable.ImmutableArray<IDevelop.Git.FileChange> ReadChanges(string commit) =>
        IDevelop.Git.CommitChanges.Read(_canvas.Project.Folder, commit, _canvas.GitEnvironment);

    /// <summary>The lines a commit of this node changed in one file, as many as a result's card shows.</summary>
    internal System.Collections.Immutable.ImmutableArray<string> ReadDiff(string commit, string path) =>
        IDevelop.Git.CommitChanges.ReadDiff(_canvas.Project.Folder, commit, path, DiffLineLimit, _canvas.GitEnvironment);

    private const int DiffLineLimit = 200;

    public bool HasAttention => Attention is not null;

    public string? AttentionLabel => Attention?.Label;

    /// <summary>Opens the node's conversation.</summary>
    public ICommand OpenConversationCommand => _openConversation;

    /// <summary>What the card's attention glyph does: it opens an agent's conversation, or selects an approval, whose request the inspector shows.</summary>
    public ICommand AttendCommand => HasAgent ? _openConversation : SelectCommand;

    public string AttendHelp => HasAgent ? "Opens the conversation" : "Shows the approval request";

    private ConversationState ConversationState
    {
        get
        {
            if (field is null)
            {
                field = _canvas.Project.ConversationOf(_canvas, Id);
                field.PropertyChanged += OnConversationStateChanged;
            }

            return field;
        }
    }

    partial void InitializeConversation()
    {
        _openConversation = new RelayCommand(() => _canvas.Project.OpenConversation(new ConversationTarget(_canvas, Id)), () => HasAgent);
    }

    private void OnConversationStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationState.Draft))
        {
            OnPropertyChanged(nameof(Draft));
            _send.NotifyCanExecuteChanged();
            _stopAndSend.NotifyCanExecuteChanged();
        }
    }
}

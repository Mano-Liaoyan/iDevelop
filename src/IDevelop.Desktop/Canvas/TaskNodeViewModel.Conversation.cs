using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the node needs from the person, and the one way the card and the inspector open its conversation.</summary>
public sealed partial class TaskNodeViewModel
{
    private ConversationState? _conversationState;
    private RelayCommand _openConversation = null!;

    /// <summary>The oldest open question, a wait for the person, or a failure. Opening the conversation leaves it.</summary>
    public Attention? Attention => Standing is not null ? null : RunTask is { } run ? WorkflowRunText.Needs(run, TitleOf) : HasAgent ? Attention.Of(_attempt) : null;

    public bool HasAttention => Attention is not null;

    public string? AttentionLabel => Attention?.Label;

    /// <summary>Opens the task's conversation, at its open question when it has one.</summary>
    public ICommand OpenConversationCommand => _openConversation;

    /// <summary>What the card's attention glyph does: it opens an agent's conversation, or selects an approval, whose request the inspector shows.</summary>
    /// <summary>A task whose recovery, updated inputs, or interrupted fix waits for the person shows its inspector, which offers the choice.</summary>
    public ICommand AttendCommand => HasAgent && !NeedsRunPanel ? _openConversation : SelectCommand;

    public string AttendHelp => !HasAgent ? "Shows the approval request" : NeedsRunPanel ? "Shows what the run needs from you" : "Opens the conversation";

    internal ConversationState ConversationState
    {
        get
        {
            if (_conversationState is null)
            {
                _conversationState = _canvas.Project.ConversationOf(_canvas, Id);
                _conversationState.PropertyChanged += OnConversationStateChanged;
            }

            return _conversationState;
        }
    }

    partial void InitializeConversation()
    {
        _openConversation = new RelayCommand(
            () => _canvas.Project.OpenConversation(new ConversationTarget(_canvas, Id, (Attention as Attention.Question)?.Request)),
            () => HasAgent);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LastAttempt) or nameof(RunTask))
            {
                OnPropertyChanged(nameof(Attention));
                OnPropertyChanged(nameof(HasAttention));
                OnPropertyChanged(nameof(AttentionLabel));
            }
        };
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

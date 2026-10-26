using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the node needs from the person, and the one way the card and the inspector open its conversation.</summary>
public sealed partial class TaskNodeViewModel
{
    private ConversationState? _conversationState;
    private RelayCommand _openConversation = null!;

    /// <summary>A wait for the person, or a failure. Opening the conversation leaves it.</summary>
    public Attention? Attention => Attention.Of(_engineState);

    public bool HasAttention => Attention is not null;

    public string? AttentionLabel => Attention?.Label;

    /// <summary>Opens the node's conversation.</summary>
    public ICommand OpenConversationCommand => _openConversation;

    /// <summary>What the card's attention glyph does: it opens an agent's conversation, or selects an approval, whose request the inspector shows.</summary>
    public ICommand AttendCommand => HasAgent ? _openConversation : SelectCommand;

    public string AttendHelp => HasAgent ? "Opens the conversation" : "Shows the approval request";

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
        _openConversation = new RelayCommand(() => _canvas.Project.OpenConversation(new ConversationTarget(_canvas, Id)), () => HasAgent);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EngineState))
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

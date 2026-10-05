using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public sealed class ConnectionViewModel : ObservableObject
{
    private readonly RelayCommand<ConnectionKind> _setKind;
    private ConnectionKind _kind;

    internal ConnectionViewModel(WorkflowCanvasViewModel canvas, ConnectionKey key, TaskNodeViewModel from, TaskNodeViewModel to, ConnectionKind kind)
    {
        Key = key;
        From = from;
        To = to;
        _kind = kind;
        _setKind = new RelayCommand<ConnectionKind>(
            newKind => canvas.Edit(new WorkflowEdit.SetConnectionKind(Key, newKind)),
            newKind => newKind != Kind);
        DeleteCommand = new RelayCommand(() => canvas.Edit(new WorkflowEdit.Delete([], [Key])));
        from.PropertyChanged += OnSourceChanged;
    }

    public ConnectionKey Key { get; }

    public TaskNodeViewModel From { get; }

    public TaskNodeViewModel To { get; }

    public ConnectionKind Kind => _kind;

    /// <summary>The kind of the node the connection leaves, whose hue the wire takes.</summary>
    public NodeKind SourceKind => From.Kind;

    public ICommand SetKindCommand => _setKind;

    public ICommand DeleteCommand { get; }

    internal void Update(ConnectionKind kind)
    {
        if (SetProperty(ref _kind, kind, nameof(Kind)))
        {
            _setKind.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Called once the connection leaves the canvas, so its source node no longer holds it.</summary>
    internal void Detach() => From.PropertyChanged -= OnSourceChanged;

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.Kind))
        {
            OnPropertyChanged(nameof(SourceKind));
        }
    }
}

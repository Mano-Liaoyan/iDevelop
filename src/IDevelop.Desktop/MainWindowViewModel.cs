using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Projects;

namespace IDevelop.Desktop;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly RelayCommand _save;
    private readonly RelayCommand _addTask;
    private WorkflowDocument? _document;
    private WorkflowCanvasViewModel? _canvas;
    private string? _status;

    public MainWindowViewModel()
    {
        _save = new RelayCommand(Save, () => _document is not null);
        _addTask = new RelayCommand(() => Canvas?.AddTaskCommand.Execute(null), () => Canvas is not null);
    }

    public WorkflowCanvasViewModel? Canvas
    {
        get => _canvas;
        private set => SetProperty(ref _canvas, value);
    }

    public string Title => _document is null
        ? "iDevelop"
        : $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(_document.ProjectFolder))}{(HasUnsavedChanges ? "*" : "")} - iDevelop";

    public bool HasUnsavedChanges => _document?.HasUnsavedChanges ?? false;

    /// <summary>The latest open or save error, or the reason the latest edit was rejected.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand SaveCommand => _save;

    public ICommand AddTaskCommand => _addTask;

    public void Open(string folder)
    {
        WorkflowDocument document;
        try
        {
            document = WorkflowDocument.Open(folder);
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = e is ProjectException ? e.Message : $"Couldn't open {folder}: {e.Message}";
            return;
        }

        _document = document;
        document.Changed += (_, _) => OnDocumentChanged();
        Canvas = new WorkflowCanvasViewModel(document, notice => Status = notice);
        Status = null;
        OnDocumentChanged();
        _save.NotifyCanExecuteChanged();
        _addTask.NotifyCanExecuteChanged();
    }

    public void Save()
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            _document.Save();
            Status = null;
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = e is ProjectException ? e.Message : $"Couldn't save: {e.Message}";
        }
    }

    private void OnDocumentChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
}

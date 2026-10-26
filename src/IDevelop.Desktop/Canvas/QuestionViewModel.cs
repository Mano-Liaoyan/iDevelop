using IDevelop.Clients;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// One question that the agent asks while its turn runs, with the choices the person picks from. The card and the
/// conversation show it, and the node's Send answers it with the choices and the draft.
/// </summary>
public sealed class QuestionViewModel
{
    private readonly AskedQuestion _question;

    internal QuestionViewModel(AskedQuestion question, Action changed)
    {
        _question = question;
        Choices = [.. question.Options.Select(option => new ChoiceViewModel(option, this, changed))];
    }

    /// <summary>The question's short label, such as "Color", or its text when it has none.</summary>
    public string Header => _question.Header.Length > 0 ? _question.Header : _question.Text;

    public IReadOnlyList<ChoiceViewModel> Choices { get; }

    internal bool MultiSelect => _question.MultiSelect;

    internal bool HasChoice => Choices.Any(choice => choice.IsChosen);

    /// <summary>The chosen choices, and the person's own <paramref name="text"/> when there is any.</summary>
    internal QuestionAnswer Answer(string text) =>
        new(_question.Id, [.. Choices.Where(choice => choice.IsChosen).Select(choice => choice.Id)], text.Length > 0 ? text : null);
}

/// <summary>One choice of a question. Choosing it unchooses the others unless the question takes several.</summary>
public sealed class ChoiceViewModel : ObservableObject
{
    private readonly QuestionViewModel _question;
    private readonly Action _changed;

    internal ChoiceViewModel(QuestionOption option, QuestionViewModel question, Action changed)
    {
        Id = option.Id;
        Label = option.Label;
        Detail = option.Detail;
        _question = question;
        _changed = changed;
    }

    internal string Id { get; }

    public string Label { get; }

    public string? Detail { get; }

    public bool IsChosen
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (value && !_question.MultiSelect)
            {
                foreach (var other in _question.Choices.Where(choice => choice != this)) other.IsChosen = false;
            }

            _changed();
        }
    }
}

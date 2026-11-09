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

    /// <param name="question">The question.</param>
    /// <param name="changed">Runs when the person's choice changes.</param>
    /// <param name="firstDigit">The digit that picks the question's first choice in the conversation.</param>
    /// <param name="showsHeader">Other questions share the attention card, so this one names itself.</param>
    internal QuestionViewModel(AskedQuestion question, Action changed, int firstDigit = 1, bool showsHeader = false)
    {
        _question = question;
        ShowsHeader = showsHeader;
        Choices = [.. question.Options.Select((option, index) => new ChoiceViewModel(option, this, changed, firstDigit + index))];
    }

    /// <summary>The question's short label, such as "Color", or its text when it has none.</summary>
    public string Header => _question.Header.Length > 0 ? _question.Header : _question.Text;

    /// <summary>The question's text under its header, or null when the header is the text.</summary>
    public string? Prompt => _question.Header.Length > 0 && _question.Text != _question.Header ? _question.Text : null;

    public bool ShowsHeader { get; }

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

    internal ChoiceViewModel(QuestionOption option, QuestionViewModel question, Action changed, int digit)
    {
        Number = digit;
        Id = option.Id;
        Label = option.Label;
        Detail = option.Detail;
        _question = question;
        _changed = changed;
    }

    internal string Id { get; }

    /// <summary>The choice's place in the attention card, which the digit key picks.</summary>
    public int Number { get; }

    /// <summary>The digit the row shows, or nothing past 9, which has no key.</summary>
    public string Digit => Number is >= 1 and <= 9 ? $"{Number}" : "";

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

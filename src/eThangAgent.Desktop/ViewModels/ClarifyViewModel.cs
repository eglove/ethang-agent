using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>
///     Interactive clarify state for the desktop: numbered option buttons, an optional
///     free-text field, and cancel. <see cref="Completion"/> settles exactly once — only
///     on a valid answer or a cancel. Transient validation failures (out-of-range option,
///     empty free text) do NOT consume the one-shot completion; they surface through the
///     observable <see cref="ValidationMessage"/> the view displays, leaving the question
///     pending. This mirrors the terminal channel's Cancelled contract while keeping bad
///     input recoverable.
/// </summary>
public sealed partial class ClarifyViewModel : ObservableObject
{
    private readonly TaskCompletionSource<Result<string>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _settled;

    public ClarifyViewModel(ClarifyQuestion question)
    {
        Question = question.Question;
        Options = question.Options;
        AllowFreeText = question.AllowFreeText;
    }

    public string Question { get; }
    public IReadOnlyList<string> Options { get; }
    public bool AllowFreeText { get; }

    [ObservableProperty]
    private string _input = "";

    /// <summary>Last transient validation error, or empty when the input is acceptable.</summary>
    [ObservableProperty]
    private string _validationMessage = "";

    /// <summary>Resolves exactly once — on a valid answer or cancel.</summary>
    public Task<Result<string>> Completion => _completion.Task;

    /// <summary>Selects an option by its 1-based display index.</summary>
    public void ChooseOption(int index)
    {
        if (index < 1 || index > Options.Count)
        {
            ValidationMessage = $"Pick an option between 1 and {Options.Count}.";
            return;
        }

        Settle(Result<string>.Success(index.ToString()));
    }

    /// <summary>Submits the trimmed free-text answer; empty input stays pending.</summary>
    public void SubmitFreeText()
    {
        var text = Input.Trim();
        if (text.Length == 0)
        {
            ValidationMessage = "Type an answer first.";
            return;
        }

        Settle(Result<string>.Success(text));
    }

    /// <summary>Cancels with the same contract as a terminal Ctrl+C.</summary>
    public void Cancel() =>
        Settle(Result<string>.Failure(new Error("Cancelled", "Cancelled by the user.")));

    /// <summary>
    ///     Surfaces an external validation failure — input routed to this question that it
    ///     cannot accept — through <see cref="ValidationMessage"/> without consuming the
    ///     one-shot completion: the question stays pending and completable.
    /// </summary>
    public void RejectInput(string message) => ValidationMessage = message;

    private void Settle(Result<string> result)
    {
        if (Interlocked.Exchange(ref _settled, 1) == 1) return;
        ValidationMessage = "";
        _completion.TrySetResult(result);
    }
}

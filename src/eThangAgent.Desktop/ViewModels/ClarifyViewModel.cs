using System.Globalization;
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
internal sealed partial class ClarifyViewModel(ClarifyQuestion question) : ObservableObject
{
  private readonly TaskCompletionSource<Result<string>> _completion =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int _settled;

  public string Question { get; } = question.Question;
  public IReadOnlyList<string> Options { get; } = question.Options;
  public bool AllowFreeText { get; } = question.AllowFreeText;

  [ObservableProperty]
  public partial string Input { get; set; } = "";

  /// <summary>Last transient validation error, or empty when the input is acceptable.</summary>
  [ObservableProperty]
  public partial string ValidationMessage { get; set; } = "";

  /// <summary>Raised exactly once when the question settles — valid answer or
  /// cancel — synchronously within <see cref="Settle"/> and on the settling
  /// thread (every production settler acts on the UI thread). The owning session
  /// view-model listens for this to close the clarify panel no matter which path
  /// settled the question.</summary>
  public event EventHandler? Settled;

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

    Settle(Result.Success(index.ToString(CultureInfo.InvariantCulture)));
  }

  /// <summary>Submits the trimmed free-text answer; empty input stays pending.</summary>
  public void SubmitFreeText()
  {
    string text = Input.Trim();
    if (text.Length == 0)
    {
      ValidationMessage = "Type an answer first.";
      return;
    }

    Settle(Result.Success(text));
  }

  /// <summary>Cancels with the same contract as a terminal Ctrl+C.</summary>
  public void Cancel() =>
      Settle(Result.Failure<string>(new DomainError("Cancelled", "Cancelled by the user.")));

  /// <summary>
  ///     Surfaces an external validation failure — input routed to this question that it
  ///     cannot accept — through <see cref="ValidationMessage"/> without consuming the
  ///     one-shot completion: the question stays pending and completable.
  /// </summary>
  public void RejectInput(string message) => ValidationMessage = message;

  private void Settle(Result<string> result)
  {
    if (Interlocked.Exchange(ref _settled, 1) == 1)
    {
      return;
    }

    ValidationMessage = "";
    _ = _completion.TrySetResult(result);
    Settled?.Invoke(this, EventArgs.Empty);
  }
}

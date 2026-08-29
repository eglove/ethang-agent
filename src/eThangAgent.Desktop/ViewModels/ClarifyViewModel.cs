using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One renderable clarify option: its 1-based display index, verbatim text,
///     and whether the keyboard highlight currently rests on it.</summary>
internal sealed record ClarifyOptionRow(int Index, string Text, bool IsSelected);

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

  /// <summary>Batch position label ("Q 2/3") when this question is one of several
  ///     tool calls in the current turn; empty for a lone question. Stamped by the
  ///     session view-model at presentation time.</summary>
  [ObservableProperty]
  public partial string ProgressLabel { get; set; } = "";

  /// <summary>The keyboard-highlighted option, 1-based; 0 when the question carries no
  ///     options. Starts on the first option so Enter alone answers the common case.</summary>
  [ObservableProperty]
  public partial int SelectedIndex { get; set; } = question.Options.Count > 0 ? 1 : 0;

  /// <summary>The options as renderable rows carrying the current keyboard highlight.
  ///     Recomputed on selection moves; the view rebinds via <see cref = "SelectedIndex"/>'s
  ///     change notification plus the explicit one raised in <see cref = "MoveSelection"/>.</summary>
  public IReadOnlyList<ClarifyOptionRow> OptionRows =>
      [.. question.Options.Select((text, i) => new ClarifyOptionRow(i + 1, text, i + 1 == SelectedIndex))];

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

  /// <summary>Moves the keyboard highlight by <paramref name = "delta"/> options, clamped
  ///     to the valid range (no wrap-around); a no-op when there are no options.</summary>
  public void MoveSelection(int delta)
  {
    if (Options.Count == 0)
    {
      return;
    }

    SelectedIndex = Math.Clamp(SelectedIndex + delta, 1, Options.Count);
    OnPropertyChanged(nameof(OptionRows));
  }

  /// <summary>Settles the question with the keyboard-selected option — the same contract
  ///     as <see cref = "ChooseOption"/>. Stays pending when nothing is selectable.</summary>
  public void ChooseSelected()
  {
    if (SelectedIndex >= 1 && SelectedIndex <= Options.Count)
    {
      ChooseOption(SelectedIndex);
    }
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

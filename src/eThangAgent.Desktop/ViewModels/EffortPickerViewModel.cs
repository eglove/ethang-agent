using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The effort confirmed in the effort picker. Null <see cref="Level"/> means
///     "model default" — the session returns to the provider's own reasoning behavior.
///     (A cancelled dialog closes with no result at all, so unchanged-vs-default never
///     collides.)</summary>
internal sealed record EffortChoice(ReasoningEffort? Level);

/// <summary>One choosable row of the effort picker: the model-default pseudo-row
///     (<see cref="Level"/> null) or a concrete reasoning-effort level.</summary>
internal sealed record EffortPickerRow(ReasoningEffort? Level, string DisplayName, string Detail);

/// <summary>View-model behind the effort picker: the model-default row plus the seven
///     reasoning levels — the same list on both providers, since both consume the
///     domain's effort vocabulary. Pure state and commands; window closing and applying
///     the choice belong to the caller. Rows are built once at construction (no search,
///     no load), so the list identity never changes and the model picker's
///     ItemsSource-desync bug class cannot occur.</summary>
internal sealed partial class EffortPickerViewModel : ObservableObject
{
  private static readonly EffortPickerRow DefaultRow =
      new(null, "Model default", "The provider's own reasoning behavior applies");

  private static readonly EffortPickerRow[] LevelRows =
  [
      new(ReasoningEffort.Max, "Max", "Deepest reasoning the model supports"),
      new(ReasoningEffort.ExtraHigh, "Extra High", "Very deep reasoning"),
      new(ReasoningEffort.High, "High", "Deep reasoning"),
      new(ReasoningEffort.Medium, "Medium", "Balanced reasoning"),
      new(ReasoningEffort.Low, "Low", "Light reasoning"),
      new(ReasoningEffort.Minimal, "Minimal", "A little reasoning"),
      new(ReasoningEffort.None, "None", "Reasoning disabled"),
  ];

  /// <summary>The row list, fixed for the picker's lifetime: the model-default row
  ///     pinned first, then the levels in the providers' documented order. The identity
  ///     is STABLE — selection changes must never swap <c>ItemsSource</c> (that resets
  ///     the ListBox mid-click and writes a stale selection back; the model picker's
  ///     click-twice desync, headless-tested in ModelPickerWindowTests).</summary>
  public IReadOnlyList<EffortPickerRow> Rows { get; } = [DefaultRow, .. LevelRows];

  /// <summary>Raised when the user confirms a row; carries the choice. The view closes
  ///     the dialog with it.</summary>
  public event EventHandler<EffortChoice>? ConfirmRequested;

  public IRelayCommand ConfirmCommand { get; }

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
  public partial EffortPickerRow? SelectedRow { get; set; }

  /// <param name="currentEffort">The session's live effort choice to pre-select
  ///     (null pre-selects the model-default row).</param>
  public EffortPickerViewModel(ReasoningEffort? currentEffort)
  {
    // The command exists before the observable property is set: setting it raises the
    // changed hook, which requeries command availability. The guard in the action is
    // load-bearing: ICommand.Execute does not consult CanExecute, and a disabled
    // button is only one of several ways this command can be invoked.
    ConfirmCommand = new RelayCommand(Confirm, () => SelectedRow is not null);
    SelectedRow = Rows.FirstOrDefault(r => r.Level == currentEffort) ?? DefaultRow;
  }

  private void Confirm()
  {
    if (SelectedRow is not null)
    {
      ConfirmRequested?.Invoke(this, new EffortChoice(SelectedRow.Level));
    }
  }
}

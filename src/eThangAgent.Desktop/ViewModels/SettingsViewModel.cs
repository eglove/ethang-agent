using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One selectable commit message style in the settings modal.</summary>
internal sealed record CommitStyleOption(CommitStyle Style, string Display)
{
  internal static readonly CommitStyleOption Conventional = new(CommitStyle.Conventional,
      "Conventional commits");
  internal static readonly CommitStyleOption Gitmoji = new(CommitStyle.Gitmoji,
      "Gitmoji");
  internal static readonly CommitStyleOption None = new(CommitStyle.None,
      "Plain (no prefix)");
}

/// <summary>One selectable z.ai endpoint mode in the settings modal.</summary>
internal sealed record ZaiEndpointModeOption(ZaiEndpointMode Mode, string Display)
{
  internal static readonly ZaiEndpointModeOption CodingPlan = new(ZaiEndpointMode.CodingPlan,
      "Coding plan (subscription)");
  internal static readonly ZaiEndpointModeOption GeneralApi = new(ZaiEndpointMode.GeneralApi,
      "General API (pay-as-you-go)");
}

/// <summary>The settings confirmed in the settings modal: the API keys, the z.ai
///     endpoint mode, and the commit style. Null keys mean "cleared" — the provider
///     stops being configured.
///     (A cancelled dialog closes with no result at all, so unchanged-vs-cleared never
///     collides.)</summary>
/// <summary>One choosable compaction summarizer: the Automatic row (persisted as
///     unset — cheapest capable resolves at compaction time) or a concrete model id.</summary>
internal sealed record CompactionModelOption(string? ModelId, string Display)
{
  internal static readonly CompactionModelOption Automatic = new(null, "Automatic (cheapest capable)");
}

internal sealed record SettingsUpdate(string? OpenRouterApiKey, string? ZaiApiKey,
    ZaiEndpointMode ZaiEndpointMode, CommitStyle CommitStyle,
    string? CompactionModelId = null, string? CompactionWorkspaceKey = null);

/// <summary>View-model behind the settings modal: the API-key fields for the two
///     providers, a reveal toggle, the z.ai endpoint mode, and their shared validation.
///     Blank means cleared; whitespace inside a key is rejected — provider keys never
///     contain any. Pure state and commands; persistence and window closing belong to
///     the caller.</summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
  /// <summary>Raised when the user confirms valid settings; carries the update. The
  ///     view closes the dialog with it.</summary>
  public event EventHandler<SettingsUpdate>? SaveRequested;

  public IRelayCommand SaveCommand { get; }

  /// <summary>The three commit styles, in display order.</summary>
  public IReadOnlyList<CommitStyleOption> CommitStyles { get; } =
      [CommitStyleOption.Conventional, CommitStyleOption.Gitmoji, CommitStyleOption.None];

  /// <summary>The compaction summarizer choices: Automatic plus the session provider's
  ///     catalog model ids.</summary>
  public IReadOnlyList<CompactionModelOption> CompactionModels { get; }

  [ObservableProperty]
  public partial CompactionModelOption SelectedCompactionModel { get; set; }

  /// <summary>The two endpoint modes, in display order.</summary>
  public IReadOnlyList<ZaiEndpointModeOption> EndpointModes { get; } =
      [ZaiEndpointModeOption.CodingPlan, ZaiEndpointModeOption.GeneralApi];

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string OpenRouterKey { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string ZaiKey { get; set; }

  [ObservableProperty]
  public partial ZaiEndpointModeOption SelectedEndpointMode { get; set; }

  [ObservableProperty]
  public partial CommitStyleOption SelectedCommitStyle { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(KeyPasswordChar))]
  public partial bool KeysVisible { get; set; }

  /// <summary>The mask the settings window applies to both key fields; null-mask char
  ///     when revealed.</summary>
  public char KeyPasswordChar => KeysVisible ? default : '•';

  /// <summary>Save is only actionable when both fields validate.</summary>
  public bool CanSave => ValidationError is null;

  /// <summary>The first validation problem across both fields, or null when clean.</summary>
  public string? ValidationError => Validate(OpenRouterKey) ?? Validate(ZaiKey);

  public SettingsViewModel(string? openRouterKey, string? zaiKey,
      ZaiEndpointMode zaiEndpointMode, CommitStyle commitStyle = CommitStyle.Conventional,
      IReadOnlyList<CompactionModelOption>? compactionModels = null,
      CompactionModelOption? selectedCompactionModel = null)
  {
    // The command exists before the observable properties: setting those raises
    // the changed hooks, which requery save availability. The guard in the action
    // is load-bearing: ICommand.Execute does not consult CanExecute, and a disabled
    // button is only one of several ways this command can be invoked.
    SaveCommand = new RelayCommand(
        () =>
        {
          if (CanSave)
          {
            SaveRequested?.Invoke(this, new SettingsUpdate(
                Normalize(OpenRouterKey), Normalize(ZaiKey), SelectedEndpointMode.Mode,
                SelectedCommitStyle.Style,
                SelectedCompactionModel.ModelId, null));
          }
        },
        () => CanSave);
    CompactionModels = compactionModels ?? [CompactionModelOption.Automatic];
    OpenRouterKey = openRouterKey ?? string.Empty;
    ZaiKey = zaiKey ?? string.Empty;
    SelectedCompactionModel = selectedCompactionModel ?? CompactionModelOption.Automatic;
    SelectedEndpointMode = zaiEndpointMode == ZaiEndpointMode.GeneralApi
        ? ZaiEndpointModeOption.GeneralApi
        : ZaiEndpointModeOption.CodingPlan;
    SelectedCommitStyle = commitStyle switch
    {
      CommitStyle.Conventional => CommitStyleOption.Conventional,
      CommitStyle.Gitmoji => CommitStyleOption.Gitmoji,
      CommitStyle.None => CommitStyleOption.None,
      _ => CommitStyleOption.Conventional, // unnamed enum values cannot occur across the typed boundary
    };
  }

  // Validation edits must requery the Save button's CanExecute.
  partial void OnOpenRouterKeyChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnZaiKeyChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  /// <summary>Returns the validation problem with <paramref name="key"/>, or null when
  ///     it is a legal entry: blank (cleared), or a trimmed non-empty value with no
  ///     internal whitespace.</summary>
  private static string? Validate(string key)
  {
    string trimmed = key.Trim();
    if (trimmed.Length == 0)
    {
      return null; // blank clears the key — legal
    }

    return trimmed.Any(char.IsWhiteSpace)
        ? "API keys cannot contain whitespace."
        : null;
  }

  private static string? Normalize(string key)
  {
    string trimmed = key.Trim();
    return trimmed.Length == 0 ? null : trimmed;
  }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;
using eThangAgent.Lsp.ACL;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One language the prompt offers to install: the catalog's display name and the
///     language id an install is asked for.</summary>
internal sealed record LanguagePromptOffer(string LanguageId, string DisplayName);

/// <summary>The per-workspace language-server prompt: names the languages this repository
///     holds and this machine cannot serve, and offers to install them all, to open the
///     language-servers page, or to be dismissed.
///     <para>IT IS A VIEW-MODEL PROPERTY, NOT A DIALOG: a workspace that needs nothing
///     renders nothing, and the session starts either way.</para>
///     <para>ONCE PER LANGUAGE PER SESSION: the prompt appears the first time detection
///     names a language this machine lacks, stays dismissed for the languages it named,
///     and re-appears only when a later detection names a language it has not named
///     before - which is how a repository that gains a language after opening still
///     prompts while a dismissed workspace stays quiet.</para>
///     <para>THE PREFERENCE IS READ ON EVERY DETECTION, so a user who turned prompting off
///     is never asked, whatever the workspace holds.</para></summary>
internal sealed partial class LanguagePromptViewModel : ObservableObject
{
  private readonly ILanguageServerCatalog _catalog;
  private readonly ILanguageServerInstaller _installer;
  private readonly IAppPreferenceStore? _preferences;
  private readonly Func<string, Task>? _openPage;
  private readonly HashSet<string> _named = new(StringComparer.OrdinalIgnoreCase);
  private bool _busy;

  /// <summary>Builds the prompt over the catalog that names a language, the installer that
  ///     performs an install, the preference store holding the user's opt-out, and the
  ///     action that opens the language-servers page. A null store reads as prompting
  ///     enabled and a null action as a Choose command that only dismisses.</summary>
  public LanguagePromptViewModel(ILanguageServerCatalog catalog, ILanguageServerInstaller installer,
      IAppPreferenceStore? preferences = null, Func<string, Task>? openPage = null)
  {
    _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    _preferences = preferences;
    _openPage = openPage;
    InstallAllCommand = new AsyncRelayCommand(InstallAllAsync, () => IsVisible && !_busy);
    ChooseCommand = new AsyncRelayCommand(ChooseAsync, () => IsVisible && !_busy);
    DismissCommand = new RelayCommand(Dismiss, () => IsVisible && !_busy);
  }

  /// <summary>True while the prompt is on screen.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Message))]
  [NotifyCanExecuteChangedFor(nameof(InstallAllCommand))]
  [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
  [NotifyCanExecuteChangedFor(nameof(DismissCommand))]
  public partial bool IsVisible { get; set; }

  /// <summary>The sentence the prompt shows: what is available, or what it is installing
  ///     right now. Empty while the prompt is hidden.</summary>
  public string Message { get; private set; } = string.Empty;

  /// <summary>The languages the prompt is currently naming.</summary>
  public IReadOnlyList<LanguagePromptOffer> Offers { get; private set; } = [];

  /// <summary>Installs every named language, one at a time, naming each in the message
  ///     while it runs. The prompt disappears when all of them succeed and stays with the
  ///     failure's reason when one does not.</summary>
  public IAsyncRelayCommand InstallAllCommand { get; }

  /// <summary>Opens the language-servers page and dismisses the prompt.</summary>
  public IAsyncRelayCommand ChooseCommand { get; }

  /// <summary>Dismisses the prompt. The languages it named are not asked about again in
  ///     this session; a language it never named still is.</summary>
  public IRelayCommand DismissCommand { get; }

  /// <summary>Applies one detection pass: the languages the workspace holds, and the
  ///     language ids this machine can already serve. Shows the prompt when the difference
  ///     names a language this session has not already named, and hides it when the
  ///     difference is empty.</summary>
  public async Task Present(IReadOnlyList<DetectedLanguage> detected,
      IReadOnlyList<string> installedLanguageIds, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(detected);
    ArgumentNullException.ThrowIfNull(installedLanguageIds);
    List<LanguagePromptOffer> missing = Missing(detected, installedLanguageIds);
    if (missing.Count == 0)
    {
      Hide();
      return;
    }

    if (await PromptingDisabledAsync(ct).ConfigureAwait(true))
    {
      return;
    }

    if (_named.IsSupersetOf(missing.Select(offer => offer.LanguageId)))
    {
      return;
    }

    _named.UnionWith(missing.Select(offer => offer.LanguageId));
    Offers = missing;
    Message = Sentence([.. missing.Select(offer => offer.DisplayName)]) + " support available";
    OnPropertyChanged(nameof(Message));
    IsVisible = true;
  }

  /// <summary>The detected languages this machine cannot serve, in detection order, named
  ///     as the catalog names them.</summary>
  private List<LanguagePromptOffer> Missing(IReadOnlyList<DetectedLanguage> detected,
      IReadOnlyList<string> installedLanguageIds)
  {
    HashSet<string> installed = new(installedLanguageIds, StringComparer.OrdinalIgnoreCase);
    return
    [
        .. detected
            .Select(language => language.LanguageId)
            .Where(languageId => !installed.Contains(languageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(languageId => new LanguagePromptOffer(languageId,
                _catalog.ForLanguage(languageId)?.DisplayName ?? languageId)),
    ];
  }

  /// <summary>The sentence naming every language in a list, in the order given:
  ///     <c>A</c>, <c>A and B</c>, <c>A, B and C</c>.</summary>
  internal static string Sentence(IReadOnlyList<string> names)
  {
    ArgumentNullException.ThrowIfNull(names);
    return names.Count switch
    {
      0 => string.Empty,
      1 => names[0],
      2 => names[0] + " and " + names[1],
      _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1],
    };
  }

  private async Task InstallAllAsync()
  {
    if (_busy || Offers.Count == 0)
    {
      return;
    }

    _busy = true;
    NotifyCommands();
    string? failure = null;
    try
    {
      foreach (LanguagePromptOffer offer in Offers)
      {
        Message = "Installing " + offer.DisplayName + "\u2026";
        OnPropertyChanged(nameof(Message));
        LspInstallResult result = await _installer.InstallAsync(offer.LanguageId, CancellationToken.None)
            .ConfigureAwait(true);
        if (result.Outcome != LspInstallOutcome.Installed)
        {
          failure = offer.DisplayName + ": " + (result.Reason ?? "the install did not complete.");
          break;
        }
      }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      failure = ex.Message;
    }
    finally
    {
      _busy = false;
      NotifyCommands();
    }

    if (failure is null)
    {
      Hide();
      return;
    }

    Message = failure;
    OnPropertyChanged(nameof(Message));
  }

  private async Task ChooseAsync()
  {
    if (_openPage is not null)
    {
      await _openPage(Offers.Count > 0 ? Offers[0].LanguageId : string.Empty).ConfigureAwait(true);
    }

    Dismiss();
  }

  private void Dismiss() => Hide();

  private void Hide()
  {
    Offers = [];
    Message = string.Empty;
    OnPropertyChanged(nameof(Message));
    IsVisible = false;
  }

  private void NotifyCommands()
  {
    InstallAllCommand.NotifyCanExecuteChanged();
    ChooseCommand.NotifyCanExecuteChanged();
    DismissCommand.NotifyCanExecuteChanged();
  }

  private async Task<bool> PromptingDisabledAsync(CancellationToken ct)
  {
    if (_preferences is null)
    {
      return false;
    }

    string? stored = await _preferences.GetAsync(LanguageServerPreferences.PromptDisabledKey, ct)
        .ConfigureAwait(true);
    return string.Equals(stored?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
  }
}

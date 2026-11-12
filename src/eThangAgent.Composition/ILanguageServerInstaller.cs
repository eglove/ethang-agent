using eThangAgent.Lsp.ACL;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition;

/// <summary>Installs and removes language servers on this machine. Every install is an
///     explicit user action from the language-servers page; nothing here is reached by the
///     daemon's automatic resolve. The install root is the app's own cache directory, so no
///     call touches the user's workspace or a global toolchain directory.</summary>
public interface ILanguageServerInstaller
{
  /// <summary>The root every install lands under.</summary>
  string InstallRoot { get; }

  /// <summary>Installs a language's server, or reports why it could not be installed. An
  ///     unknown language id is a refusal naming the language, never an exception.</summary>
  Task<LspInstallResult> InstallAsync(string languageId, CancellationToken ct);

  /// <summary>Deletes the install directory of one language's server and clears its
  ///     installed entry. Nothing outside that directory is touched.</summary>
  Task RemoveAsync(string languageId, CancellationToken ct);

  /// <summary>Reads the languages this machine has installed. The answer is a value the
  ///     caller holds, so a read can never report a machine that has not been read yet.
  ///     <para>THERE IS NO PARAMETERLESS READ. The store is the only install state, and a
  ///     container's installer starts out knowing none of it, so a read that did not go to
  ///     the store would answer for a machine with nothing installed.</para></summary>
  Task<InstalledLanguageServers> ReadAsync(CancellationToken ct);

  /// <summary>The prerequisite executable a language needs and this machine lacks, or null
  ///     when the language needs none or the machine has it.</summary>
  string? MissingPrerequisiteExecutable(string languageId);
}

/// <summary>The languages this machine has installed, as one read of the preference store.
///     <para>A CALLER HOLDS THIS VALUE rather than asking for state on demand: there is no
///     way to obtain one without having read the store, so a container that has read nothing
///     cannot report an empty machine that is not empty.</para></summary>
public sealed class InstalledLanguageServers
{
  /// <summary>Nothing installed.</summary>
  public static InstalledLanguageServers None { get; } = new InstalledLanguageServers([]);

  private readonly IReadOnlyList<InstalledLanguageServer> _entries;

  private InstalledLanguageServers(IReadOnlyList<InstalledLanguageServer> entries)
  {
    _entries = entries;
    LanguageIds = [.. entries.Select(entry => entry.LanguageId)];
  }

  /// <summary>The installed language ids, in stored order.</summary>
  public IReadOnlyList<string> LanguageIds { get; }

  /// <summary>Builds a snapshot from stored entries.</summary>
  public static InstalledLanguageServers From(IReadOnlyList<InstalledLanguageServer> entries)
  {
    ArgumentNullException.ThrowIfNull(entries);
    return entries.Count == 0 ? None : new InstalledLanguageServers(entries);
  }

  /// <summary>The version recorded for a language, or null when it has no stored entry.
  ///     A blank language id names no entry.</summary>
  public string? Version(string languageId) =>
      string.IsNullOrWhiteSpace(languageId)
          ? null
          : _entries
              .FirstOrDefault(entry => string.Equals(entry.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))
              ?.Version;
}

/// <summary>The installer over the app's own language-server strategies and preference
///     store. Installs go straight to the row's strategy, so a user-initiated install is
///     never suppressed by a failure the daemon remembered earlier in the process.</summary>
public sealed class LanguageServerInstaller : ILanguageServerInstaller
{
  private readonly ILanguageServerCatalog _catalog;
  private readonly IAppPreferenceStore _preferences;
  private readonly IReadOnlyDictionary<LspInstallKind, ILspInstallStrategy> _strategies;
  private readonly LspPrerequisiteProbe _probe;
  private readonly Lock _gate = new();
  private readonly Dictionary<string, Task<LspInstallResult>> _inflight = [with(StringComparer.OrdinalIgnoreCase)];

  public LanguageServerInstaller(
      ILanguageServerCatalog catalog,
      IAppPreferenceStore preferences,
      string installRoot,
      IReadOnlyDictionary<LspInstallKind, ILspInstallStrategy>? strategies = null,
      LspPrerequisiteProbe? probe = null)
  {
    _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
    InstallRoot = installRoot;
    _strategies = strategies ?? LspServerInstaller.DefaultStrategies();
    _probe = probe ?? LspPrerequisiteProbe.CreateDefault();
  }

  public string InstallRoot { get; }

  /// <summary>Reads the stored preference and answers what it names. Every call goes to the
  ///     store, so the answer is this machine's state at the moment of the call rather than a
  ///     copy some earlier caller happened to leave behind.</summary>
  public async Task<InstalledLanguageServers> ReadAsync(CancellationToken ct)
  {
    string? stored = await _preferences.GetAsync(LanguageServerPreferences.InstalledKey, ct).ConfigureAwait(false);
    return LanguageServerPreferences.Parse(stored) is { IsSuccess: true } parsed
        ? InstalledLanguageServers.From(parsed.Value)
        : InstalledLanguageServers.None;
  }

  public string? MissingPrerequisiteExecutable(string languageId)
  {
    LspLanguageRow? row = _catalog.ForLanguage(languageId);
    if (row is null || string.IsNullOrWhiteSpace(row.PrerequisiteExecutable))
    {
      return null;
    }

    PrerequisiteProbeResult probed = _probe.Probe(row.PrerequisiteExecutable);
    return probed.Status == PrerequisiteStatus.Present ? null : row.PrerequisiteExecutable;
  }

  public Task<LspInstallResult> InstallAsync(string languageId, CancellationToken ct)
  {
    LspLanguageRow? row = _catalog.ForLanguage(languageId);
    if (row is null)
    {
      return Task.FromResult(new LspInstallResult(LspInstallOutcome.DownloadFailed, null,
          $"Unknown language '{languageId}': this app serves no such language."));
    }

    Task<LspInstallResult> attempt;
    lock (_gate)
    {
      if (!_inflight.TryGetValue(row.LanguageId, out Task<LspInstallResult>? running))
      {
        running = InstallCoreAsync(row, ct);
        _inflight[row.LanguageId] = running;
      }

      attempt = running;
    }

    return attempt;
  }

  public async Task RemoveAsync(string languageId, CancellationToken ct)
  {
    LspLanguageRow? row = _catalog.ForLanguage(languageId);
    if (row is null)
    {
      return;
    }

    string directory = InstallDirectory(row);
    if (Directory.Exists(directory))
    {
      Directory.Delete(directory, recursive: true);
    }

    await RemoveFromPreferenceAsync(row.LanguageId, ct).ConfigureAwait(false);
  }

  private async Task<LspInstallResult> InstallCoreAsync(LspLanguageRow row, CancellationToken ct)
  {
    try
    {
      ILspInstallStrategy strategy = _strategies.TryGetValue(row.InstallKind, out ILspInstallStrategy? found)
          ? found
          : throw new ArgumentException("No install strategy for " + row.InstallKind, nameof(row));
      LspInstallResult result = await strategy.InstallAsync(row, InstallRoot, ct).ConfigureAwait(false);
      if (result.Outcome == LspInstallOutcome.Installed)
      {
        await AddToPreferenceAsync(row, ct).ConfigureAwait(false);
      }

      return result;
    }
    finally
    {
      lock (_gate)
      {
        _ = _inflight.Remove(row.LanguageId);
      }
    }
  }

  /// <summary>The directory one row's install occupies: keyed by package and version for a
  ///     downloaded row, by language and version for a toolchain row.</summary>
  private string InstallDirectory(LspLanguageRow row) =>
      row.InstallKind == LspInstallKind.Toolchain
          ? Path.Combine(InstallRoot, row.LanguageId, row.Version)
          : Path.Combine(InstallRoot, row.PackageId, row.Version);

  private async Task AddToPreferenceAsync(LspLanguageRow row, CancellationToken ct)
  {
    string? stored = await _preferences.GetAsync(LanguageServerPreferences.InstalledKey, ct).ConfigureAwait(false);
    IReadOnlyList<InstalledLanguageServer> entries = LanguageServerPreferences.Parse(stored) is { IsSuccess: true } parsed
        ? parsed.Value
        : [];
    List<InstalledLanguageServer> updated =
    [
        .. entries.Where(entry => !string.Equals(entry.LanguageId, row.LanguageId, StringComparison.OrdinalIgnoreCase)),
        new InstalledLanguageServer(row.LanguageId, row.Version),
    ];
    _ = await _preferences.SetAsync(LanguageServerPreferences.InstalledKey,
        LanguageServerPreferences.Serialize(updated), ct).ConfigureAwait(false);
  }

  private async Task RemoveFromPreferenceAsync(string languageId, CancellationToken ct)
  {
    string? stored = await _preferences.GetAsync(LanguageServerPreferences.InstalledKey, ct).ConfigureAwait(false);
    IReadOnlyList<InstalledLanguageServer> entries = LanguageServerPreferences.Parse(stored) is { IsSuccess: true } parsed
        ? parsed.Value
        : [];
    List<InstalledLanguageServer> updated =
    [
        .. entries.Where(entry => !string.Equals(entry.LanguageId, languageId, StringComparison.OrdinalIgnoreCase)),
    ];
    _ = updated.Count == 0
        ? await _preferences.DeleteAsync(LanguageServerPreferences.InstalledKey, ct).ConfigureAwait(false)
        : await _preferences.SetAsync(LanguageServerPreferences.InstalledKey,
            LanguageServerPreferences.Serialize(updated), ct).ConfigureAwait(false);
  }
}

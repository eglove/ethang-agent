namespace eThangAgent.Lsp.ACL;

/// <summary>Installs a language server into an app-owned cache by handing the row to the
///     strategy its install kind names. Concurrent callers for one row share a single
///     attempt and all observe its outcome, an already-installed row is never fetched again,
///     and a failed install is remembered for the life of the process, which keeps an offline
///     machine from re-attempting on every resolve.</summary>
public sealed class LspServerInstaller
{
  private readonly string _root;
  private readonly IReadOnlyDictionary<LspInstallKind, ILspInstallStrategy> _strategies;
  private readonly Lock _gate = new();
  private readonly Dictionary<string, Task<LspInstallResult>> _inflight = [with(StringComparer.Ordinal)];
  private readonly Dictionary<string, LspInstallOutcome> _failed = [with(StringComparer.Ordinal)];

  public LspServerInstaller(
      string root,
      IReadOnlyDictionary<LspInstallKind, ILspInstallStrategy> strategies)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    _root = root;
    _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
  }

  /// <summary>The default cache root: the app's own local data directory.</summary>
  public static string DefaultRoot =>
      Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "eThangAgent",
          "language-servers");

  /// <summary>The default installer: the real downloader and the real toolchain probe.</summary>
  public static LspServerInstaller CreateDefault() => new(DefaultRoot, DefaultStrategies());

  /// <summary>One strategy per install kind, each reaching the network or a toolchain for
  ///     real.</summary>
  public static IReadOnlyDictionary<LspInstallKind, ILspInstallStrategy> DefaultStrategies() =>
      new Dictionary<LspInstallKind, ILspInstallStrategy>
      {
        [LspInstallKind.NuGet] = LspNuGetInstallStrategy.CreateDefault(),
        [LspInstallKind.Archive] = LspArchiveInstallStrategy.CreateDefault(),
        [LspInstallKind.Toolchain] = LspToolchainInstallStrategy.CreateDefault(),
      };

  /// <summary>The installed entry point, or null when nothing verified is on disk.</summary>
  public string? ResolveInstalled(LspLanguageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    return LspInstallLayout.InstalledEntryPoint(row, _root);
  }

  /// <summary>Installs the row, or reports why it could not be installed. Concurrent
  /// callers for the same row share one attempt and all observe its outcome.</summary>
  public async Task<LspInstallResult> InstallAsync(LspLanguageRow row, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(row);
    string key = LspInstallLayout.Key(row);
    Task<LspInstallResult> attempt;
    lock (_gate)
    {
      if (_failed.TryGetValue(key, out LspInstallOutcome remembered))
      {
        return new LspInstallResult(remembered, row);
      }

      if (!_inflight.TryGetValue(key, out Task<LspInstallResult>? running))
      {
        running = InstallCoreAsync(row, key, cancellationToken);
        _inflight[key] = running;
      }

      attempt = running;
    }

    return await attempt.ConfigureAwait(false);
  }

  private async Task<LspInstallResult> InstallCoreAsync(
      LspLanguageRow row,
      string key,
      CancellationToken cancellationToken)
  {
    ILspInstallStrategy strategy = StrategyFor(row);
    LspInstallResult result = await strategy.InstallAsync(row, _root, cancellationToken).ConfigureAwait(false);
    if (result.Outcome != LspInstallOutcome.Installed)
    {
      lock (_gate)
      {
        _failed[key] = result.Outcome;
      }
    }

    return result;
  }

  private ILspInstallStrategy StrategyFor(LspLanguageRow row) =>
      _strategies.TryGetValue(row.InstallKind, out ILspInstallStrategy? strategy)
          ? strategy
          : throw new ArgumentException("No install strategy for " + row.InstallKind, nameof(row));
}

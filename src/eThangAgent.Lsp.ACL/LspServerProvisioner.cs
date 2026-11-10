namespace eThangAgent.Lsp.ACL;

/// <summary>Produces a launch for a language: an already-resolvable server wins, and an
///     absent one is installed into the app-owned cache. A server that cannot be resolved
///     or installed yields null, which callers treat as a silent Unavailable no-op.</summary>
public sealed class LspServerProvisioner
{
  private readonly Func<LspLanguageRow, string?> _resolveExisting;
  private readonly LspServerInstaller _installer;

  public LspServerProvisioner(Func<LspLanguageRow, string?> resolveExisting, string installRoot)
      : this(resolveExisting, LspServerInstaller.CreateDefault(), installRoot)
  {
  }

  public LspServerProvisioner(
      Func<LspLanguageRow, string?> resolveExisting,
      LspServerInstaller installer,
      string installRoot)
  {
    _ = installRoot;
    _resolveExisting = resolveExisting ?? throw new ArgumentNullException(nameof(resolveExisting));
    _installer = installer ?? throw new ArgumentNullException(nameof(installer));
  }

  /// <summary>The default provisioner: discovery first, then the real install strategies.</summary>
  public static LspServerProvisioner CreateDefault(string appDirectory, string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    LspServerResolver resolver = LspServerResolver.CreateDefault();
    return new LspServerProvisioner(
        row => resolver.Resolve(appDirectory, row),
        LspServerInstaller.CreateDefault(),
        LspServerInstaller.DefaultRoot);
  }

  /// <summary>The launch for a server already on this machine, or null when nothing is. No
  ///     install is attempted, so a language nobody installed starts nothing.</summary>
  public LspServerLaunch? ResolveExisting(LspLanguageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    string? existing = _resolveExisting(row) ?? _installer.ResolveInstalled(row);
    return string.IsNullOrWhiteSpace(existing) ? null : new LspServerLaunch(row.LanguageId, existing, row.Version);
  }

  public async Task<LspServerLaunch?> ResolveOrInstallAsync(LspLanguageRow row, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(row);
    LspServerLaunch? existing = ResolveExisting(row);
    if (existing is not null)
    {
      return existing;
    }

    LspInstallResult result = await _installer.InstallAsync(row, cancellationToken).ConfigureAwait(false);
    if (result.Outcome != LspInstallOutcome.Installed)
    {
      return null;
    }

    string? installed = _installer.ResolveInstalled(row);
    return installed is null ? null : new LspServerLaunch(row.LanguageId, installed, row.Version);
  }
}

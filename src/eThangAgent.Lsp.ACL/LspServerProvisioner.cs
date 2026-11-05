namespace eThangAgent.Lsp.ACL;

/// <summary>Produces a launch for a language: an already-resolvable server wins, and an
/// absent one is downloaded into the app-owned cache. A server that cannot be resolved
/// or installed yields null, which callers treat as a silent Unavailable no-op.</summary>
public sealed class LspServerProvisioner
{
  private readonly Func<LspServerPackage, Task<byte[]?>> _download;
  private readonly Func<LspServerPackage, string?> _resolveExisting;
  private readonly LspServerInstaller _installer;

  public LspServerProvisioner(
      Func<LspServerPackage, Task<byte[]?>> download,
      Func<LspServerPackage, string?> resolveExisting,
      string installRoot)
  {
    _download = download ?? throw new ArgumentNullException(nameof(download));
    _resolveExisting = resolveExisting ?? throw new ArgumentNullException(nameof(resolveExisting));
    _installer = new LspServerInstaller(installRoot, download);
  }

  /// <summary>The default provisioner: discovery first, then the real downloader.</summary>
  public static LspServerProvisioner CreateDefault(string appDirectory, string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    LspServerResolver resolver = LspServerResolver.CreateDefault();
    return new LspServerProvisioner(
        package => LspServerDownloader.DownloadAsync(package, CancellationToken.None),
        package => resolver.Resolve(appDirectory) ?? LspServerInstallerAt(appDirectory, package),
        LspServerInstaller.DefaultRoot);
  }

  private static string? LspServerInstallerAt(string appDirectory, LspServerPackage package)
  {
    _ = appDirectory;
    return new LspServerInstaller(LspServerInstaller.DefaultRoot, _ => Task.FromResult<byte[]?>(null))
        .ResolveInstalled(package);
  }

  public async Task<LspServerLaunch?> ResolveOrInstallAsync(LspServerPackage package, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(package);
    string? existing = _resolveExisting(package);
    if (!string.IsNullOrWhiteSpace(existing))
    {
      return new LspServerLaunch(package.LanguageId, existing, package.Version);
    }
    LspInstallResult result = await _installer.InstallAsync(package, cancellationToken).ConfigureAwait(false);
    if (result.Outcome != LspInstallOutcome.Installed)
    {
      return null;
    }
    string? installed = _installer.ResolveInstalled(package);
    return installed is null ? null : new LspServerLaunch(package.LanguageId, installed, package.Version);
  }
}

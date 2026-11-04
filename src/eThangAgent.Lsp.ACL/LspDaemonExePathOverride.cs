namespace eThangAgent.Lsp.ACL;

/// <summary>The daemon executable path a host resolved, published into the container so
/// every consumer of the language-server daemon agrees on it. A host that overrides it
/// (a test, or a deployment that ships the daemon elsewhere) sets <see cref="Path"/>
/// before the first advisory query.</summary>
public sealed class LspDaemonExePathOverride
{
  private string? _path;

  /// <summary>The resolved daemon path: the override when set, else the default
  /// locator's answer.</summary>
  public string Path
  {
    get => _path ??= LspDaemonExeLocator.CreateDefault().Resolve();
    set
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(value);
      _path = value;
    }
  }
}

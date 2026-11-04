namespace eThangAgent.Lsp.ACL;

/// <summary>Locates the language-server daemon executable (issue #361): the copy shipped
/// NEXT TO THE APP first, then the repo's live build output, then the deployed path so a
/// caller's not-found error names where the daemon is expected to live.</summary>
public sealed class LspDaemonExeLocator
{
  public const string ExeName = "eThangAgent.Lsp.Host.exe";
  private static readonly string[] ConfigurationPreference = ["Debug", "Release"];
  private static readonly string[] TfmPreference = ["net10.0-windows", "net10.0"];

  private readonly Func<string, bool> _fileExists;
  private readonly string? _repoRoot;
  private readonly string _appDirectory;

  public LspDaemonExeLocator(Func<string, bool> fileExists, string? repoRoot, string appDirectory)
  {
    _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
    _repoRoot = repoRoot;
    _appDirectory = appDirectory;
  }

  public static LspDaemonExeLocator CreateDefault() =>
      new(File.Exists, FindRepoRoot(typeof(LspDaemonExeLocator).Assembly.Location), AppContext.BaseDirectory);

  /// <summary>The checkout containing the given assembly, or null when it lives outside
  /// any checkout (a published app) - an ordinary answer, never a fault.</summary>
  public static string? FindRepoRoot(string assemblyLocation)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(assemblyLocation);
    DirectoryInfo? dir = new(assemblyLocation);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir?.FullName;
  }

  public string Resolve()
  {
    string deployed = Path.Combine(_appDirectory, ExeName);
    if (_fileExists(deployed))
    {
      return deployed;
    }

    if (!string.IsNullOrWhiteSpace(_repoRoot))
    {
      foreach (string configuration in ConfigurationPreference)
      {
        foreach (string tfm in TfmPreference)
        {
          string candidate = Path.Combine(_repoRoot, "src", "eThangAgent.Lsp.Host", "bin", configuration, tfm, ExeName);
          if (_fileExists(candidate))
          {
            return candidate;
          }
        }
      }
    }

    return deployed;
  }
}

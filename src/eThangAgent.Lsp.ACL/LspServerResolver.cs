using System.Diagnostics;

namespace eThangAgent.Lsp.ACL;

/// <summary>Resolved server launch: language, absolute server path, pinned version.</summary>
public sealed record LspServerLaunch(string LanguageId, string ServerPath, string Version);

/// <summary>Resolves the C# language server: bundled beside the app, then PATH, then
/// known install roots, then the app-owned download cache — first hit wins, cached per
/// process. Only the catalog's server name is ever resolved; a binary named inside the
/// workspace is never executed (trust rule). Absent server returns null, which callers
/// treat as a silent Unavailable no-op.</summary>
public sealed class LspServerResolver
{
  public static string CSharpServerVersion => LspServerCatalog.CSharp.Version;
  public static string CSharpServerFileName => LspServerCatalog.CSharp.ServerFileName;

  private static readonly HashSet<string> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
  {
    LspServerCatalog.CSharp.ServerFileName,
  };

  private readonly Func<string, bool> _fileExists;
  private readonly Func<string, string?> _whereOnPath;
  private readonly IReadOnlyList<string> _knownRoots;
  private readonly string _serverFileName;
  private readonly Func<string?> _downloadedServer;
  private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

  public LspServerResolver(
    Func<string, bool> fileExists,
    Func<string, string?> whereOnPath,
    IReadOnlyList<string> knownRoots,
    string serverFileName,
    Func<string?>? downloadedServer = null)
  {
    _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    _whereOnPath = whereOnPath ?? throw new ArgumentNullException(nameof(whereOnPath));
    _knownRoots = knownRoots ?? throw new ArgumentNullException(nameof(knownRoots));
    if (string.IsNullOrWhiteSpace(serverFileName) || !WellKnownNames.Contains(serverFileName))
    {
      throw new ArgumentException("Only well-known server names may be resolved.", nameof(serverFileName));
    }
    _serverFileName = serverFileName;
    _downloadedServer = downloadedServer ?? (() => null);
  }

  public static LspServerResolver CreateDefault()
    => new(
        File.Exists,
        WhereOnPath,
        DefaultKnownRoots(),
        CSharpServerFileName,
        () => new LspServerInstaller(LspServerInstaller.DefaultRoot, _ => Task.FromResult<byte[]?>(null))
            .ResolveInstalled(LspServerCatalog.CSharp));

  public static IReadOnlyList<string> DefaultKnownRoots()
  {
    List<string> roots = [];
    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    if (!string.IsNullOrWhiteSpace(programFiles))
    {
      roots.Add(Path.Combine(programFiles, "roslyn-language-server"));
    }
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(localAppData))
    {
      roots.Add(Path.Combine(localAppData, "roslyn-language-server"));
    }
    return roots;
  }

  public string? Resolve(string appDirectory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
    if (_cache.TryGetValue(appDirectory, out string? cached))
    {
      return cached;
    }
    string? result = ResolveUncached(appDirectory);
    _cache[appDirectory] = result;
    return result;
  }

  private string? ResolveUncached(string appDirectory)
  {
    string bundled = Path.Combine(appDirectory, _serverFileName);
    if (_fileExists(bundled))
    {
      return bundled;
    }
    string? onPath = _whereOnPath(_serverFileName);
    if (!string.IsNullOrWhiteSpace(onPath) && _fileExists(onPath))
    {
      return onPath;
    }
    foreach (string root in _knownRoots)
    {
      string candidate = Path.Combine(root, _serverFileName);
      if (_fileExists(candidate))
      {
        return candidate;
      }
    }
    string? downloaded = _downloadedServer();
    return !string.IsNullOrWhiteSpace(downloaded) && _fileExists(downloaded) ? downloaded : null;
  }

  public LspServerLaunch? TryResolveForWorkspace(string appDirectory, string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    if (LspLanguageDetector.DetectLanguage(workspaceRoot) != "csharp")
    {
      return null;
    }
    string? server = Resolve(appDirectory);
    return server is null ? null : new LspServerLaunch("csharp", server, CSharpServerVersion);
  }

  private static string? WhereOnPath(string exe)
  {
#pragma warning disable CA1031 // Named decision: where.exe may be absent; discovery falls through.
    try
    {
      ProcessStartInfo psi = new(Path.Combine(Environment.SystemDirectory, "where.exe"), exe)
      {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
      };
      using Process? p = Process.Start(psi);
      if (p is null) return null;
      string? first = p.StandardOutput.ReadLine();
      _ = p.WaitForExit(5000);
      return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(first) ? first.Trim() : null;
    }
    catch (Exception)
    {
      return null;
    }
#pragma warning restore CA1031
  }
}

namespace eThangAgent.Lsp.ACL;

public static class CodeDetector
{
  private static readonly HashSet<string> ManifestNames =
  [
    with(StringComparer.OrdinalIgnoreCase),
    ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".vcxproj",
    "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "pom.xml",
    "composer.json", "Gemfile", "requirements.txt"
  ];

  private static readonly HashSet<string> CodeExtensions =
  [
    with(StringComparer.OrdinalIgnoreCase),
    ".cs", ".fs", ".vb", ".cpp", ".h", ".hpp", ".c", ".ts", ".tsx",
    ".js", ".jsx", ".mjs", ".py", ".java", ".go", ".rs", ".rb", ".php",
    ".swift", ".kt", ".scala", ".sh", ".ps1", ".sql"
  ];

  private static readonly HashSet<string> ExcludedDirectories =
  [
    with(StringComparer.OrdinalIgnoreCase),
    ".git", ".svn", ".hg", "bin", "obj", "node_modules", "packages",
    ".worktrees", ".venv", "venv", "__pycache__", ".idea", ".vs", ".vscode", ".nuget"
  ];

  private const int MaxFiles = 2000;

  public static CodeDetectionResult Detect(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    if (!Directory.Exists(workspaceRoot))
    {
      return CodeDetectionResult.NotFound;
    }

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
    int count = 0;
    bool found = false;
    bool complete = Walk(workspaceRoot, workspaceRoot, ref count, cts.Token, ref found);
    if (found)
    {
      return CodeDetectionResult.Found;
    }

    return complete ? CodeDetectionResult.NotFound : CodeDetectionResult.Inconclusive;
  }

  private static bool Walk(string root, string directory, ref int count, CancellationToken ct, ref bool found)
  {
    IEnumerable<string> files;
    try { files = Directory.EnumerateFiles(directory); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

    foreach (string file in files)
    {
      if (ct.IsCancellationRequested) return false;
      if (count >= MaxFiles) return false;
      count++;
      string name = Path.GetFileName(file);
      string ext = Path.GetExtension(file);
      if (ManifestNames.Contains(name) || ManifestNames.Contains(ext) || CodeExtensions.Contains(ext))
      {
        found = true;
        return true;
      }
    }

    IEnumerable<string> subdirs;
    try { subdirs = Directory.EnumerateDirectories(directory); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

    bool complete = true;
    foreach (string child in subdirs)
    {
      if (ExcludedDirectories.Contains(Path.GetFileName(child))) continue;
      if (!Walk(root, child, ref count, ct, ref found))
      {
        if (count >= MaxFiles || ct.IsCancellationRequested) return false;
        complete = false;
      }
    }
    return complete;
  }
}

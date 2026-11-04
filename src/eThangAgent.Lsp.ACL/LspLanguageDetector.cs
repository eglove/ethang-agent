namespace eThangAgent.Lsp.ACL;

/// <summary>Language detection from project files (issue #355): .sln/.slnx/.csproj
/// implies C#. A workspace with no project file returns null, so no server
/// starts and no prompt shows. Other languages are recorded for later slices.</summary>
public static class LspLanguageDetector
{
  private static readonly HashSet<string> CSharpProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
  {
    ".sln", ".slnx", ".csproj",
  };

  public static string? DetectLanguage(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    if (!Directory.Exists(workspaceRoot))
    {
      return null;
    }
    return HasProjectFile(workspaceRoot) ? "csharp" : null;
  }

  public static bool HasProjectFile(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    if (!Directory.Exists(workspaceRoot))
    {
      return false;
    }
    try
    {
      foreach (string file in Directory.EnumerateFiles(workspaceRoot))
      {
        if (CSharpProjectExtensions.Contains(Path.GetExtension(file)))
        {
          return true;
        }
      }
    }
#pragma warning disable CA1031 // Named decision: unreadable workspace means no project file, never a crash.
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return false;
    }
#pragma warning restore CA1031
    return false;
  }

  public static LspServerConfig CSharpConfig(string serverPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);
    return new LspServerConfig("csharp", serverPath, [], [".cs"]);
  }
}

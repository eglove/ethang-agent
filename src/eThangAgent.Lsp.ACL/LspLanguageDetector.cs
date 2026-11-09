namespace eThangAgent.Lsp.ACL;

/// <summary>Language detection from project files (issue #355): .sln/.slnx/.csproj
/// implies C#. A workspace with no project file returns null, so no server starts and no
/// prompt shows. Other languages are recorded for later slices.
///     <para>THE PROJECT FILE IS SEARCHED FOR, not assumed to be at the root. A workspace
///     is whatever directory the user opened: a project that keeps its manifest in
///     <c>src/</c> is ordinary, and looking only at the top level declared that workspace
///     to hold no code - so no server started and every query was silent.</para></summary>
public static class LspLanguageDetector
{
  private static readonly HashSet<string> CSharpProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
  {
    ".sln", ".slnx", ".csproj",
  };

  public static string? DetectLanguage(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    return HasProjectFile(workspaceRoot) ? "csharp" : null;
  }

  /// <summary>True when the workspace holds a C# project manifest anywhere a project may
  ///     keep it, so a workspace is served whatever its layout.</summary>
  public static bool HasProjectFile(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    return WorkspaceManifest.Find(workspaceRoot, path => CSharpProjectExtensions.Contains(Path.GetExtension(path))) is not null;
  }

  public static LspServerConfig CSharpConfig(string serverPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);
    return new LspServerConfig("csharp", serverPath, [], [".cs"]);
  }
}

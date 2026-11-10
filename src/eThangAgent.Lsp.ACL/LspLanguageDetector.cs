namespace eThangAgent.Lsp.ACL;

/// <summary>Language detection from project files: a .sln/.slnx/.csproj implies C#. A
///     workspace with no project file returns null, so no server starts and no prompt shows.
///     <para>THE PROJECT FILE IS SEARCHED FOR, not assumed to be at the root. A workspace
///     is whatever directory the user opened: a project that keeps its manifest in
///     <c>src/</c> is ordinary, and looking only at the top level declared that workspace
///     to hold no code - so no server started and every query was silent.</para>
///     <para>The extensions and manifest names come from the catalog's C# row, so this type
///     and the row cannot disagree about what a C# workspace is.</para></summary>
public static class LspLanguageDetector
{
  public static string? DetectLanguage(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    return HasProjectFile(workspaceRoot) ? CSharpRow.LanguageId : null;
  }

  /// <summary>True when the workspace holds a C# project manifest anywhere a project may
  ///     keep it, so a workspace is served whatever its layout.</summary>
  public static bool HasProjectFile(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    return WorkspaceManifest.Find(workspaceRoot, CSharpRow.HasManifestName) is not null;
  }

  public static LspServerConfig CSharpConfig(string serverPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);
    return new LspServerConfig(CSharpRow.LanguageId, serverPath, [], CSharpRow.Extensions);
  }

  private static LspLanguageRow CSharpRow =>
      LspServerCatalog.ForLanguage("csharp")
      ?? throw new InvalidOperationException("The catalog carries no C# row.");
}

namespace eThangAgent.Lsp.ACL;

/// <summary>The servers this app can install, one row per language. Adding a language
/// is a row here, not a new code path: the downloader reads the row and knows nothing
/// about any particular server.</summary>
public static class LspServerCatalog
{
  public static LspServerPackage CSharp { get; } = new(
      LanguageId: "csharp",
      PackageId: "roslyn-language-server.win-x64",
      Version: "5.13.0-1.26514.10",
      Sha512: "+bE9gCtusnlR0SVlFdfc0remupBvkNf8y2NKNp7CrCDEfbZMgCfucEMyQ/c56BxxmVy8nKDxnYpL5KnziLWo1Q==",
      EntryPointRelativePath: "tools/net10.0/win-x64",
      ServerFileName: "roslyn-language-server.exe");

  /// <summary>Every installable server, keyed by the language it serves.</summary>
  public static IReadOnlyList<LspServerPackage> All { get; } = [CSharp];

  /// <summary>The package serving a language, or null when the language has none. A
  /// language with no row is the ordinary absent-server case, never an error.</summary>
  public static LspServerPackage? ForLanguage(string? languageId)
  {
    if (string.IsNullOrWhiteSpace(languageId)) return null;
    foreach (LspServerPackage package in All)
    {
      if (string.Equals(package.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))
      {
        return package;
      }
    }
    return null;
  }
}

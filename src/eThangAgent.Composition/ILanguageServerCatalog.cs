using eThangAgent.Lsp.ACL;

namespace eThangAgent.Composition;

/// <summary>The language servers this app can install, one row per language. The desktop
///     reads the catalog through this seam so no view-model type names a language-server
///     type of its own.</summary>
public interface ILanguageServerCatalog
{
  /// <summary>Every language this app can serve, in the order the settings page lists them.</summary>
  IReadOnlyList<LspLanguageRow> All { get; }

  /// <summary>The row serving a language, or null when the language has none.</summary>
  LspLanguageRow? ForLanguage(string? languageId);
}

/// <summary>The catalog over the app's own language-server rows.</summary>
public sealed class LanguageServerCatalog : ILanguageServerCatalog
{
  /// <summary>The shared instance: the catalog is immutable data, so one serves every caller.</summary>
  public static LanguageServerCatalog Default { get; } = new();

  public IReadOnlyList<LspLanguageRow> All => LspServerCatalog.All;

  public LspLanguageRow? ForLanguage(string? languageId) => LspServerCatalog.ForLanguage(languageId);
}

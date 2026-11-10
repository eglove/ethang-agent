namespace eThangAgent.Lsp.ACL;

/// <summary>Which language claims a path. A path is claimed by the FIRST catalog row whose
///     extensions carry it, so an overlap (JavaScript belongs to the TypeScript row) resolves
///     to one language rather than to every row that could serve it.</summary>
public static class LspLanguageRouter
{
  /// <summary>The language whose row claims <paramref name="path"/>'s extension, or null
  ///     when no language does.</summary>
  public static string? ForPath(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    string extension = Path.GetExtension(path);
    if (extension.Length == 0)
    {
      return null;
    }

    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      if (row.Claims(path))
      {
        return row.LanguageId;
      }
    }

    return null;
  }
}

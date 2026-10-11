namespace eThangAgent.Documents.ACL;

/// <summary>Named caps for the docx surface (task 4): file size, pagination, edit
///     batch size, markdown input, and the SDK's own pathological-XML guard. The
///     caps exist so a hostile or accidental file cannot exhaust memory - zip-bomb
///     and deep-XML protection lives here, not in the domain.</summary>
public static class DocxLimits
{
  /// <summary>Maximum accepted .docx file size (50 MB).</summary>
  public const long MaxFileBytes = 50L * 1024 * 1024;

  /// <summary>Maximum blocks per read page.</summary>
  public const int MaxBlocksPerPage = 400;

  /// <summary>Default blocks per read page.</summary>
  public const int DefaultBlocksPerPage = 200;

  /// <summary>Maximum operations per edit batch.</summary>
  public const int MaxOperationsPerEdit = 50;

  /// <summary>Maximum markdown characters accepted by create.</summary>
  public const int MaxMarkdownChars = 100_000;

  /// <summary>OpenSettings.MaxCharactersInPart - the SDK's own pathological-XML cap.</summary>
  public const int MaxCharactersInPart = 10_000_000;

  /// <summary>Maximum rows a created table may carry.</summary>
  public const int MaxTableRows = 5_000;
}

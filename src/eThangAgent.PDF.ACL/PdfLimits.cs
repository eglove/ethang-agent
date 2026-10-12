namespace eThangAgent.PDF.ACL;

/// <summary>Named caps and defaults for the PDF surface (task 4).
///     The caps exist so a hostile or accidental file cannot exhaust memory or execution budgets.</summary>
public static class PdfLimits
{
  /// <summary>Maximum accepted PDF file size (100 MB).</summary>
  public const long MaxFileBytes = 100L * 1024 * 1024;

  /// <summary>Maximum pages per read call.</summary>
  public const int MaxPagesPerRead = 100;

  /// <summary>Default pages per read call.</summary>
  public const int DefaultPagesPerRead = 20;

  /// <summary>Maximum pages per render call.</summary>
  public const int MaxRenderPagesPerCall = 10;

  /// <summary>Default rendering DPI.</summary>
  public const int DefaultDpi = 150;

  /// <summary>Maximum rendering DPI.</summary>
  public const int MaxDpi = 300;

  /// <summary>Maximum operations per manipulation batch.</summary>
  public const int MaxOperationsPerBatch = 50;

  /// <summary>Maximum markdown characters accepted by create.</summary>
  public const int MaxMarkdownChars = 100_000;
}

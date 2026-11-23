namespace eThangAgent.Presentation.ACL;

/// <summary>Named caps for the pptx surface: file size, slide count, and the SDK's own
///     pathological-XML guard. The caps exist so a hostile or accidental deck cannot
///     exhaust memory - zip-bomb and deep-XML protection lives here, not in the domain.
///     </summary>
public static class PptxLimits
{
  /// <summary>Maximum accepted .pptx file size (100 MiB), measured before any part is
  ///     read.</summary>
  public const long MaxFileBytes = 104_857_600;

  /// <summary>Maximum slides a deck may carry. A deck past this is refused as
  ///     OversizePresentation before its slides are walked.</summary>
  public const int MaxSlides = 1000;

  /// <summary>Maximum operations in one edit batch.</summary>
  public const int MaxOperationsPerEdit = 50;

  /// <summary>OpenSettings.MaxCharactersInPart - the SDK's own pathological-XML cap.
  ///     </summary>
  public const int MaxCharactersInPart = 10_000_000;
}

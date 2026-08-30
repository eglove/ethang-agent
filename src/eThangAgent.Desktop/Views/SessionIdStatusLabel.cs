namespace eThangAgent.Desktop.Views;

/// <summary>The status-bar session-id button's label contract: "Sess. Id" plus a copy
///     glyph at rest, the same label with a checkmark while a copy just succeeded.
///     Shared by the view and its tests so the strings exist in exactly one place.</summary>
internal static class SessionIdStatusLabel
{
  public const string Label = "Sess. Id";
  public const char CopyGlyph = '\u29C9';
  public const char SuccessGlyph = '\u2713';

  public static string Default => $"{Label} {CopyGlyph}";
  public static string Success => $"{Label} {SuccessGlyph}";
}

using Avalonia.Media;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Dark-theme-first palette for rendered code. Static brushes shared by
///     every render path - they are only ever read on the UI thread. The Night Owl
///     theme item in the grand plan is where these become themeable.</summary>
internal static class MarkdownPalette
{
  public static IBrush BrushFor(MarkdownCodeTokenKind kind) => kind switch
  {
    MarkdownCodeTokenKind.Comment => Comment,
    MarkdownCodeTokenKind.String => String,
    MarkdownCodeTokenKind.Number => Number,
    MarkdownCodeTokenKind.Keyword => Keyword,
    MarkdownCodeTokenKind.Default => Default,
    _ => Default,
  };

  private static IBrush Default { get; } = Brushes.Gainsboro;
  private static IBrush Comment { get; } = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
  private static IBrush String { get; } = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
  private static IBrush Number { get; } = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
  private static IBrush Keyword { get; } = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
}

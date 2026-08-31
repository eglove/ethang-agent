using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace eThangAgent.Desktop.Markdown;

/// <summary>A rendered markdown link: blue underlined text that opens its URL
///     through <see cref="MarkdownLinkLauncher"/> on click. The URL is captured at
///     render time and never re-read, so re-renders cannot launch a stale target.
///     Embedded in a TextBlock through InlineUIContainer, mirroring the table Grid.
///     Hand cursor signals clickability; Unloaded resets to the default cursor.</summary>
internal sealed class MarkdownLinkBlock : TextBlock
{
  public MarkdownLinkBlock(string text, string url)
  {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(url);
    Text = text;
    Url = url;
    // Transparent background makes the whole control a click target: TextBlock
    // hit-tests glyphs only, which leaves the padding around the text dead.
    Background = Brushes.Transparent;
    Foreground = Brushes.DodgerBlue;
    // Per-instance decoration, never the shared static: in headless tests multiple
    // Avalonia scopes share one process, and a render of a shared TextDecoration
    // owned by another scope's UI thread throws cross-thread. Same is true of any
    // future multi-window scenario - the control owns what it draws.
    TextDecorations = [new TextDecoration { Location = TextDecorationLocation.Underline }];
  }

  public string Url { get; }

  /// <summary>Marks left-button press handled so the host SelectableTextBlock's
  ///     selection handler neither starts a selection drag nor captures the pointer -
  ///     either would swallow the release that launches the link.</summary>
  protected override void OnPointerPressed(PointerPressedEventArgs e)
  {
    base.OnPointerPressed(e);
    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
    {
      e.Handled = true;
    }
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e)
  {
    base.OnPointerReleased(e);
    if (e.InitialPressMouseButton == MouseButton.Left)
    {
      _ = MarkdownLinkLauncher.TryOpen(Url);
    }
  }

  /// <summary>The hand cursor needs the platform's cursor factory, so it is
  ///     assigned on load - the constructor stays pure for headless rendering.</summary>
  protected override void OnLoaded(RoutedEventArgs e)
  {
    base.OnLoaded(e);
    Cursor = new Cursor(StandardCursorType.Hand);
  }

  protected override void OnUnloaded(RoutedEventArgs e)
  {
    base.OnUnloaded(e);
    Cursor = Cursor.Default;
  }
}

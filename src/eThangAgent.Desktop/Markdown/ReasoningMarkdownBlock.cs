using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Transcript block for streamed reasoning: renders plain while open
///     (streaming) and parses markdown once on close - the same contract as
///     <see cref="AssistantMarkdownBlock"/>. A named decision, not an oversight:
///     re-laying-out the full accumulated markdown per streamed delta sends
///     Avalonia's line breaker (TextFormatterImpl.PerformTextWrapping) into a
///     pathological wrap loop on large streamed blocks - the transcript wedge the
///     StreamFollowScrollTests guard against. Styling stays italic/dim via
///     constructor defaults so bindings cannot clobber them.</summary>
internal class ReasoningMarkdownBlock : SelectableTextBlock
{
  public static readonly StyledProperty<string> MarkdownTextProperty =
      AvaloniaProperty.Register<ReasoningMarkdownBlock, string>(nameof(MarkdownText), string.Empty);

  public static readonly StyledProperty<bool> IsOpenProperty =
      AvaloniaProperty.Register<ReasoningMarkdownBlock, bool>(nameof(IsOpen), true);

  public string MarkdownText
  {
    get => GetValue(MarkdownTextProperty);
    set => SetValue(MarkdownTextProperty, value);
  }

  public bool IsOpen
  {
    get => GetValue(IsOpenProperty);
    set => SetValue(IsOpenProperty, value);
  }

  public ReasoningMarkdownBlock()
  {
    FontStyle = FontStyle.Italic;
    Opacity = 0.7;
  }

  // Self-rendering: property changes drive every render. The open path is a single
  // plain Run; the markdown parser runs only on close.
  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
  {
    base.OnPropertyChanged(change);
    if (change.Property == MarkdownTextProperty || change.Property == IsOpenProperty)
    {
      Render();
    }
  }

  /// <summary>Rebuilds the inline content from the current MarkdownText/IsOpen
  ///     state (inline rebuild is layout-independent).</summary>
  public void Render()
  {
    Inlines ??= [];
    Inlines.Clear();
    if (IsOpen)
    {
      Inlines.Add(new Run(MarkdownText));
      return;
    }

    MarkdownInlineRenderer.Render(Inlines, MarkdownParser.Parse(MarkdownText ?? string.Empty));
  }
}

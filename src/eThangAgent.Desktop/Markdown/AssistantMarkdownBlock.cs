using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Transcript block for assistant text: renders plain while open (streaming,
///     no per-token re-parse), markdown once closed. Owns MarkdownText/IsOpen styled
///     properties; the inherited Text stays unused so it cannot fight Inlines.
///     Rendering happens in <see cref="Render"/> - called by data templates when the
///     entry settles - rather than on property-change callbacks.</summary>
internal class AssistantMarkdownBlock : SelectableTextBlock
{
  public static readonly StyledProperty<string> MarkdownTextProperty =
      AvaloniaProperty.Register<AssistantMarkdownBlock, string>(nameof(MarkdownText), string.Empty);

  public static readonly StyledProperty<bool> IsOpenProperty =
      AvaloniaProperty.Register<AssistantMarkdownBlock, bool>(nameof(IsOpen), true);

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

  /// <summary>Rebuilds the inline content from the current MarkdownText/IsOpen state.
  ///     Awaiting layout is unnecessary: inline rebuild is layout-independent.</summary>
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

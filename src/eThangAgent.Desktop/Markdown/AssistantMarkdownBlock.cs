using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Transcript block for assistant text: renders plain while open (streaming,
///     no per-token re-parse), markdown once closed. Owns MarkdownText/IsOpen styled
///     properties; the inherited Text stays unused so it cannot fight Inlines.
///     Rendering runs in <see cref="Render"/> whenever MarkdownText or IsOpen changes:
///     streaming deltas replace the entry record, so the re-pushed binding drives each
///     re-render - the open path is a single plain Run, and the markdown parser runs
///     only on close.</summary>
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

  // Self-rendering: nothing outside this class may call Render (the shipped bug was
  // a control that waited for a caller that never came - invisible assistant text).
  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
  {
    base.OnPropertyChanged(change);
    if (change.Property == MarkdownTextProperty || change.Property == IsOpenProperty)
    {
      Render();
    }
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

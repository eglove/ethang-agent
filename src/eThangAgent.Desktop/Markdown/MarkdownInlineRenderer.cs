using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Renders a <see cref="MarkdownDocument"/> as styled inlines (bold /
///     monospace / link styling, heading sizes, line breaks) plus tables embedded as
///     Grids through InlineUIContainer. Pure C# over Avalonia document types -
///     headless-testable; no XAML template logic.</summary>
internal static class MarkdownInlineRenderer
{
  private static readonly FontFamily MonoFont = new("Consolas, Courier New");

  private const double BlockSpacerHeight = 8;
  private const double HeadingSpacerHeight = 12;
  private static readonly IBrush PanelBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

  public static void Render(InlineCollection target, MarkdownDocument document)
  {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(document);
    for (int i = 0; i < document.Blocks.Count; i++)
    {
      RenderBlock(target, document.Blocks[i]);
      if (i < document.Blocks.Count - 1)
      {
        AddSpacer(target, document.Blocks[i]);
      }
    }
  }

  /// <summary>Vertical gap between consecutive blocks: an empty fixed-height Border
  ///     embedded through InlineUIContainer, so the gap participates in line layout
  ///     without any per-block control templates. Headings get extra leading.</summary>
  private static void AddSpacer(InlineCollection target, Block previous)
  {
    double height = previous is HeadingBlock ? HeadingSpacerHeight : BlockSpacerHeight;
    target.Add(new InlineUIContainer { Child = new Border { Height = height } });
  }

  private static void RenderBlock(InlineCollection target, Block block)
  {
    switch (block)
    {
      case HeadingBlock heading:
        RenderInlineRun(target, heading.Inlines, bold: true, size: HeadingSize(heading.Level));
        EndLine(target);
        break;
      case ParagraphBlock paragraph:
        RenderInlineRun(target, paragraph.Inlines);
        EndLine(target);
        break;
      case CodeBlock code:
        RenderCode(target, code);
        break;
      case ListBlock list:
        foreach (IReadOnlyList<Inline> item in list.Items)
        {
          target.Add(new Run("• ") { Foreground = Brushes.Gray });
          RenderInlineRun(target, item);
          EndLine(target);
        }
        break;
      case TableBlock table:
        RenderTable(target, table);
        break;
      default:
        break;
    }
  }

  /// <summary>Renders a table as a fresh Grid (new instance per render, so the
  ///     Inlines.Clear() re-render path never meets a stale parent) embedded through
  ///     an InlineUIContainer. Rows are Auto-height, columns share width evenly
  ///     (Star); header cells render bold.</summary>
  /// <summary>Known languages tokenize into a padded, rounded panel of per-line
  ///     TextBlocks (one Run per token span); unknown or empty languages keep the
  ///     original plain monospace runs so nothing regresses for unlisted code.</summary>
  private static void RenderCode(InlineCollection target, CodeBlock code)
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize(code.Text, code.Language);
    if (tokens.Length == 1 && tokens[0].Kind == MarkdownCodeTokenKind.Default)
    {
      foreach (string codeLine in code.Text.Split('\n'))
      {
        target.Add(new Run(codeLine) { FontFamily = MonoFont });
        EndLine(target);
      }

      return;
    }

    target.Add(new InlineUIContainer { Child = BuildCodePanel(tokens) });
    EndLine(target);
  }

  /// <summary>Splits token spans at newlines into per-line TextBlocks: a span that
  ///     spans lines (block comment, verbatim string) colors every line it touches.</summary>
  private static Border BuildCodePanel(MarkdownCodeToken[] tokens)
  {
    StackPanel stack = new();
    TextBlock line = NewCodeLine();
    foreach (MarkdownCodeToken token in tokens)
    {
      string[] segments = token.Text.Split('\n');
      for (int s = 0; s < segments.Length; s++)
      {
        if (s > 0)
        {
          stack.Children.Add(line);
          line = NewCodeLine();
        }

        if (segments[s].Length > 0)
        {
          line.Inlines ??= [];
          line.Inlines.Add(new Run(segments[s])
          {
            FontFamily = MonoFont,
            Foreground = MarkdownPalette.BrushFor(token.Kind),
          });
        }
      }
    }

    stack.Children.Add(line);
    return new Border
    {
      Background = PanelBackground,
      CornerRadius = new CornerRadius(6),
      Padding = new Thickness(8, 6),
      Child = stack,
    };
  }

  private static TextBlock NewCodeLine() => new() { TextWrapping = TextWrapping.NoWrap };

  private static void RenderTable(InlineCollection target, TableBlock table)
  {
    int columnCount = Math.Max(table.Header.Cells.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Cells.Count));
    if (columnCount == 0)
    {
      return;
    }

    Grid grid = new();
    for (int c = 0; c < columnCount; c++)
    {
      grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
    }

    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    for (int r = 0; r < table.Rows.Count; r++)
    {
      grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    }

    AddRow(grid, 0, table.Header.Cells, columnCount, bold: true);
    for (int r = 0; r < table.Rows.Count; r++)
    {
      AddRow(grid, r + 1, table.Rows[r].Cells, columnCount, bold: false);
    }

    target.Add(new InlineUIContainer { Child = grid });
    EndLine(target);
  }

  private static void AddRow(Grid grid, int rowIndex, IReadOnlyList<TableCell> cells, int columnCount, bool bold)
  {
    for (int c = 0; c < columnCount; c++)
    {
      TextBlock cell = new() { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
      if (c < cells.Count)
      {
        cell.Inlines ??= [];
        RenderInlineRun(cell.Inlines, cells[c].Inlines);
      }

      Grid.SetRow(cell, rowIndex);
      Grid.SetColumn(cell, c);
      grid.Children.Add(cell);
    }
  }

  private static void RenderInlineRun(InlineCollection target, IReadOnlyList<Inline> inlines,
      bool bold = false, double size = 0)
  {
    foreach (Inline inline in inlines)
    {
      switch (inline)
      {
        case TextSpan text:
          target.Add(MakeRun(text.Text, bold, size));
          break;
        case BoldSpan boldSpan:
          RenderInlineRun(target, boldSpan.Children, bold: true, size);
          break;
        case ItalicSpan italic:
          ItalicRun(target, italic, bold, size);
          break;
        case CodeSpan code:
          target.Add(new Run(code.Code) { FontFamily = MonoFont });
          break;
        case LinkSpan link:
          target.Add(new InlineUIContainer { Child = new MarkdownLinkBlock(link.Text, link.Url) });
          break;
        default:
          break;
      }
    }
  }

  private static void ItalicRun(InlineCollection target, ItalicSpan italic, bool bold, double size)
  {
    foreach (Inline child in italic.Children)
    {
      switch (child)
      {
        case TextSpan text:
          Run run = MakeRun(text.Text, bold, size);
          run.FontStyle = FontStyle.Italic;
          target.Add(run);
          break;
        case CodeSpan code:
          target.Add(new Run(code.Code) { FontFamily = MonoFont });
          break;
        default:
          RenderInlineRun(target, [child], bold, size);
          break;
      }
    }
  }

  private static Run MakeRun(string text, bool bold, double size)
  {
    Run run = new(text);
    if (bold)
    {
      run.FontWeight = FontWeight.Bold;
    }

    if (size > 0)
    {
      run.FontSize = size;
    }

    return run;
  }

  private static double HeadingSize(int level)
  {
    return level switch
    {
      1 => 22,
      2 => 19,
      3 => 17,
      _ => 15.5,
    };
  }

  private static void EndLine(InlineCollection target) => target.Add(new LineBreak());
}

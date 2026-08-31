using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

// The mono stack the renderer uses; pinned here so tests fail if it drifts.
internal static class TestFonts
{
  public static readonly FontFamily Mono = new("Consolas, Courier New");
}

public class MarkdownInlineRendererTests
{
  [Fact]
  public void Bold_Renders_With_Bold_Typeface()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("**bold**"));
    Run run = Assert.IsType<Run>(Assert.Single(inlines.OfType<Run>()));
    Assert.Equal("bold", run.Text);
    Assert.Equal(FontWeight.Bold, run.FontWeight);
  }

  [Fact]
  public void Heading_Renders_Larger_And_Bold()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("## Head"));
    Run run = Assert.IsType<Run>(Assert.Single(inlines.OfType<Run>()));
    Assert.Equal("Head", run.Text);
    Assert.Equal(FontWeight.Bold, run.FontWeight);
    Assert.True(run.FontSize > 14);
  }

  [Fact]
  public void Inline_Code_Renders_With_Monospace_Font()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("a `code` b"));
    Run[] runs = [.. inlines.OfType<Run>()];
    Assert.Equal(3, runs.Length);
    Run code = runs[1];
    Assert.Equal("code", code.Text);
    Assert.Equal(TestFonts.Mono, code.FontFamily);
  }

  [Fact]
  public void Link_Renders_Clickable_Blue_Underlined_Block()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("[docs](https://example.com)"));
    MarkdownLinkBlock link = Assert.IsType<MarkdownLinkBlock>(
        Assert.Single(inlines.OfType<InlineUIContainer>()).Child);
    Assert.Equal("docs", link.Text);
    Assert.Equal("https://example.com", link.Url);
    Assert.Equal(Brushes.DodgerBlue, link.Foreground);
    Assert.True(link.TextDecorations is not null && link.TextDecorations.Count > 0);
  }

  [Fact]
  public void Paragraph_And_List_Render_As_Lines()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("first para\n\n- one\n- two"));
    // A LineBreak ends the paragraph; each list item is a bullet run plus a text run.
    Assert.Equal(3, inlines.OfType<LineBreak>().Count());
    Assert.Equal(2, inlines.OfType<Run>().Count(x => x.Text == "• "));
    Assert.Equal(2, inlines.OfType<Run>().Count(x => x.Text is "one" or "two"));
  }

  [Fact]
  public void CodeBlock_Renders_Monospace_Lines()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("```\nlet x = 1\n```"));
    Run run = Assert.IsType<Run>(Assert.Single(inlines.OfType<Run>()));
    Assert.Contains("let x = 1", run.Text, StringComparison.Ordinal);
    Assert.Equal(TestFonts.Mono, run.FontFamily);
  }

  [Fact]
  public void CodeBlock_Unknown_Language_Keeps_Plain_Monospace_Runs()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("```\nlet x = 1\n```"));
    Run run = Assert.IsType<Run>(Assert.Single(inlines.OfType<Run>()));
    Assert.Contains("let x = 1", run.Text, StringComparison.Ordinal);
    Assert.Equal(TestFonts.Mono, run.FontFamily);
    Assert.DoesNotContain(inlines, i => i is InlineUIContainer);
  }

  [Fact]
  public void CodeBlock_Known_Language_Renders_Panel_With_Token_Colors()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("```csharp\n// c\nint x = 1;\n```"));
    InlineUIContainer container = Assert.Single(inlines.OfType<InlineUIContainer>());
    Border panel = Assert.IsType<Border>(container.Child);
    StackPanel stack = Assert.IsType<StackPanel>(panel.Child);
    // two code lines: the comment, then the statement
    Assert.Equal(2, stack.Children.Count);
    TextBlock commentLine = Assert.IsType<TextBlock>(stack.Children[0]);
    Run commentRun = Assert.IsType<Run>(Assert.Single((commentLine.Inlines ?? []).OfType<Run>()));
    Assert.Equal("// c", commentRun.Text);
    Assert.Equal(MarkdownPalette.BrushFor(MarkdownCodeTokenKind.Comment), commentRun.Foreground);
    TextBlock codeLine = Assert.IsType<TextBlock>(stack.Children[1]);
    Run[] runs = [.. (codeLine.Inlines ?? []).OfType<Run>()];
    // int | space | x | " = " | 1 | ; — identifier, spaces, punctuation stay uncolored
    Assert.Equal(6, runs.Length);
    Assert.All(runs, r => Assert.Equal(TestFonts.Mono, r.FontFamily));
    Assert.Equal("int", runs[0].Text);
    Assert.Equal(MarkdownPalette.BrushFor(MarkdownCodeTokenKind.Keyword), runs[0].Foreground);
    Assert.Equal("x", runs[2].Text);
    Assert.Equal("1", runs[4].Text);
    Assert.Equal(MarkdownPalette.BrushFor(MarkdownCodeTokenKind.Number), runs[4].Foreground);
  }

  [Fact]
  public void CodeBlock_Tokens_Split_Into_Lines_Keeping_Text_Exact()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("```python\n# h\ny = \"s\"\n```"));
    InlineUIContainer container = Assert.Single(inlines.OfType<InlineUIContainer>());
    StackPanel stack = Assert.IsType<StackPanel>(Assert.IsType<Border>(container.Child).Child);
    Assert.Equal(2, stack.Children.Count); // two code lines
    TextBlock first = Assert.IsType<TextBlock>(stack.Children[0]);
    Assert.Equal("# h", string.Concat((first.Inlines ?? []).OfType<Run>().Select(r => r.Text)));
    TextBlock second = Assert.IsType<TextBlock>(stack.Children[1]);
    Assert.Equal("y = \"s\"", string.Concat((second.Inlines ?? []).OfType<Run>().Select(r => r.Text)));
    Run str = Assert.Single((second.Inlines ?? []).OfType<Run>(), r => r.Foreground == MarkdownPalette.BrushFor(MarkdownCodeTokenKind.String));
    Assert.Equal("\"s\"", str.Text);
  }

  [Fact]
  public void CodeBlock_Panel_Has_Padding_And_Background()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("```json\n{\"k\": 1}\n```"));
    InlineUIContainer container = Assert.Single(inlines.OfType<InlineUIContainer>());
    Border panel = Assert.IsType<Border>(container.Child);
    Assert.True(panel.Padding.Left > 0 && panel.Padding.Top > 0);
    Assert.NotNull(panel.Background);
  }

  [Fact]
  public void Table_Renders_Grid_With_Row_And_Column_Measure()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("A | B\n--- | ---\n1 | 2"));
    InlineUIContainer container = Assert.Single(inlines.OfType<InlineUIContainer>());
    Grid grid = Assert.IsType<Grid>(container.Child);
    Assert.Equal(2, grid.RowDefinitions.Count);
    Assert.Equal(2, grid.ColumnDefinitions.Count);
    Assert.Equal(4, grid.Children.Count); // 2 rows x 2 columns of cells
  }

  [Fact]
  public void Table_Header_Cells_Render_Bold_Above_Body_Cells()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("A | B\n--- | ---\n1 | 2"));
    InlineUIContainer container = inlines.OfType<InlineUIContainer>().First();
    Grid grid = Assert.IsType<Grid>(container.Child);
    TextBlock header = Assert.IsType<TextBlock>(grid.Children[0]);
    Assert.Equal(FontWeight.Bold, header.FontWeight);
    TextBlock body = Assert.IsType<TextBlock>(grid.Children[2]);
    Assert.NotEqual(FontWeight.Bold, body.FontWeight);
  }

  [Fact]
  public void Table_Cell_Keeps_Monospace_CodeSpan_Styling()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("Cmd | N\n--- | ---\n`git` | x"));
    InlineUIContainer container = inlines.OfType<InlineUIContainer>().First();
    Grid grid = Assert.IsType<Grid>(container.Child);
    TextBlock cell = Assert.IsType<TextBlock>(grid.Children[2]);
    Run codeRun = Assert.IsType<Run>(Assert.Single((cell.Inlines ?? []).OfType<Run>()));
    Assert.Equal(TestFonts.Mono, codeRun.FontFamily);
  }

  [Fact]
  public void Spacer_Separates_Blocks_And_Is_Absent_At_Edges()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("## Head\n\nBody"));
    object[] seq = [.. inlines.Cast<object>()];
    InlineUIContainer spacer = Assert.Single(seq.OfType<InlineUIContainer>()); // one gap between the two blocks
    int at = Array.IndexOf(seq, spacer);
    Assert.True(at > 0 && at < seq.Length - 1, $"spacer must sit between blocks, not at an edge (index {at} of {seq.Length})");
    _ = Assert.IsType<LineBreak>(seq[at - 1]); // directly after the previous block's line break
  }

  [Fact]
  public void Single_Block_Renders_No_Spacer()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("only text"));
    Assert.DoesNotContain(inlines, i => i is InlineUIContainer);
  }

  [Fact]
  public void Spacer_Has_Fixed_Positive_Height()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("a\n\nb"));
    Border spacer = Assert.IsType<Border>(Assert.Single(inlines.OfType<InlineUIContainer>()).Child);
    Assert.True(spacer.Height > 0, $"spacer must have positive height, got {spacer.Height}");
  }

  [Fact]
  public void Spacer_After_Heading_Is_Taller_Than_Between_Paragraphs()
  {
    InlineCollection afterHeading = [];
    MarkdownInlineRenderer.Render(afterHeading, MarkdownParser.Parse("## H\n\np"));
    InlineCollection betweenParas = [];
    MarkdownInlineRenderer.Render(betweenParas, MarkdownParser.Parse("p1\n\np2"));
    Border heading = Assert.IsType<Border>(afterHeading.OfType<InlineUIContainer>().Single().Child);
    Border paragraph = Assert.IsType<Border>(betweenParas.OfType<InlineUIContainer>().Single().Child);
    Assert.True(heading.Height > paragraph.Height, $"heading spacer {heading.Height} should exceed paragraph spacer {paragraph.Height}");
  }

  [Fact]
  public void Table_Embedded_Between_Inlines_With_LineBreak()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("before\n\nA | B\n--- | ---\n1 | 2\n\nafter"));
    // paragraph run(s), LineBreak, spacer, table container, LineBreak, paragraph run(s)
    object[] seq = [.. inlines.Cast<object>()];
    InlineUIContainer table = seq.OfType<InlineUIContainer>().Single(c => c.Child is Grid);
    int at = Array.IndexOf(seq, table);
    _ = Assert.IsType<LineBreak>(seq[at - 2]); // previous block ends its line
    Border gap = Assert.IsType<Border>(Assert.IsType<InlineUIContainer>(seq[at - 1]).Child);
    Assert.True(gap.Height > 0); // spacer separates paragraph from table
    _ = Assert.IsType<LineBreak>(seq[at + 1]); // table sits on its own line
    Assert.Contains(seq.OfType<Run>(), r => r.Text == "after");
  }
}

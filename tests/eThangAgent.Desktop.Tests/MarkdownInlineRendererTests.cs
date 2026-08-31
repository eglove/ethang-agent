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
  public void Link_Renders_Blue_Underlined_Run()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("[docs](https://example.com)"));
    Run run = Assert.IsType<Run>(Assert.Single(inlines.OfType<Run>()));
    Assert.Equal("docs", run.Text);
    Assert.True(run.TextDecorations is not null && run.TextDecorations.Count > 0);
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
  public void Table_Embedded_Between_Inlines_With_LineBreak()
  {
    InlineCollection inlines = [];
    MarkdownInlineRenderer.Render(inlines, MarkdownParser.Parse("before\n\nA | B\n--- | ---\n1 | 2\n\nafter"));
    // paragraph run(s), LineBreak, table container, LineBreak, paragraph run(s)
    object[] seq = [.. inlines.Cast<object>()];
    int containerAt = Array.IndexOf(seq, seq.OfType<InlineUIContainer>().Single());
    Assert.True(containerAt > 0);
    _ = Assert.IsType<LineBreak>(seq[containerAt - 1]);
    _ = Assert.IsType<LineBreak>(seq[containerAt + 1]);
    Assert.Contains(seq.OfType<Run>(), r => r.Text == "after");
  }
}

using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

public class MarkdownParserTests
{
  [Fact]
  public void Empty_Input_Produces_No_Blocks()
  {
    MarkdownDocument doc = MarkdownParser.Parse("");
    Assert.Empty(doc.Blocks);
  }

  [Theory]
  [InlineData("# Title", 1, "Title")]
  [InlineData("## Sub", 2, "Sub")]
  [InlineData("###### Deep", 6, "Deep")]
  public void Headings_Parse_With_Level(string source, int expectedLevel, string expectedText)
  {
    MarkdownDocument doc = MarkdownParser.Parse(source);
    HeadingBlock h = Assert.IsType<HeadingBlock>(Assert.Single(doc.Blocks));
    Assert.Equal(expectedLevel, h.Level);
    Assert.Equal(expectedText, Assert.IsType<TextSpan>(Assert.Single(h.Inlines)).Text);
  }

  [Fact]
  public void Heading_Requires_Space_After_Hashes()
  {
    MarkdownDocument doc = MarkdownParser.Parse("#hashtag");
    ParagraphBlock p = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
    Assert.Equal("#hashtag", Assert.IsType<TextSpan>(Assert.Single(p.Inlines)).Text);
  }

  [Fact]
  public void Fenced_Code_Block_Is_Verbatim_With_Language()
  {
    MarkdownDocument doc = MarkdownParser.Parse("```csharp\nvar x = 1;\nif (x > 0) { }\n```");
    CodeBlock code = Assert.IsType<CodeBlock>(Assert.Single(doc.Blocks));
    Assert.Equal("csharp", code.Language);
    Assert.Equal("var x = 1;\nif (x > 0) { }", code.Text);
  }

  [Fact]
  public void Unterminated_Fence_Consumes_Rest_As_Code()
  {
    MarkdownDocument doc = MarkdownParser.Parse("```\nstill code\nnever closed");
    CodeBlock code = Assert.IsType<CodeBlock>(Assert.Single(doc.Blocks));
    Assert.Equal("", code.Language);
    Assert.Equal("still code\nnever closed", code.Text);
  }

  [Fact]
  public void Inline_Formats_Parse_Inside_A_Paragraph()
  {
    MarkdownDocument doc = MarkdownParser.Parse("plain **bold** *it* `code`");
    ParagraphBlock p = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
    Assert.Equal(
    [
        typeof(TextSpan),   // "plain "
        typeof(BoldSpan),   // bold
        typeof(TextSpan),   // " "
        typeof(ItalicSpan), // it
        typeof(TextSpan),   // " "
        typeof(CodeSpan),   // code
    ], p.Inlines.Select(i => i.GetType()).ToList());
    Assert.Equal("plain ", Assert.IsType<TextSpan>(p.Inlines[0]).Text);
    Assert.Equal("bold", Assert.IsType<TextSpan>(Assert.Single(((BoldSpan)p.Inlines[1]).Children)).Text);
    Assert.Equal("it", Assert.IsType<TextSpan>(Assert.Single(((ItalicSpan)p.Inlines[3]).Children)).Text);
    Assert.Equal("code", Assert.IsType<CodeSpan>(p.Inlines[5]).Code);
  }

  [Fact]
  public void Unclosed_Markers_Render_As_Literal_Text()
  {
    MarkdownDocument doc = MarkdownParser.Parse("a ** b ` c");
    ParagraphBlock p = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
    TextSpan text = Assert.IsType<TextSpan>(Assert.Single(p.Inlines));
    Assert.Equal("a ** b ` c", text.Text);
  }

  [Fact]
  public void Link_Parses_Text_And_Url()
  {
    MarkdownDocument doc = MarkdownParser.Parse("see [docs](https://example.com/a) now");
    ParagraphBlock p = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
    LinkSpan link = Assert.IsType<LinkSpan>(p.Inlines[1]);
    Assert.Equal("docs", link.Text);
    Assert.Equal("https://example.com/a", link.Url);
  }

  [Fact]
  public void Bare_Url_Becomes_A_Link()
  {
    MarkdownDocument doc = MarkdownParser.Parse("visit https://example.com/x today");
    ParagraphBlock p = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
    LinkSpan link = Assert.IsType<LinkSpan>(p.Inlines[1]);
    Assert.Equal("https://example.com/x", link.Url);
    Assert.Equal("https://example.com/x", link.Text);
  }

  [Fact]
  public void Bullet_List_Groups_Consecutive_Items()
  {
    MarkdownDocument doc = MarkdownParser.Parse("- one\n* two\n- three");
    ListBlock list = Assert.IsType<ListBlock>(Assert.Single(doc.Blocks));
    Assert.False(list.Ordered);
    Assert.Equal(3, list.Items.Count);
    Assert.Equal("one", Assert.IsType<TextSpan>(Assert.Single(list.Items[0])).Text);
    Assert.Equal("two", Assert.IsType<TextSpan>(Assert.Single(list.Items[1])).Text);
  }

  [Fact]
  public void Ordered_List_Recognized()
  {
    MarkdownDocument doc = MarkdownParser.Parse("1. first\n2. second");
    ListBlock list = Assert.IsType<ListBlock>(Assert.Single(doc.Blocks));
    Assert.True(list.Ordered);
    Assert.Equal(2, list.Items.Count);
  }

  [Fact]
  public void Consecutive_Text_Lines_Join_Into_One_Paragraph()
  {
    MarkdownDocument doc = MarkdownParser.Parse("line one\nline two\n\nline three");
    Assert.Equal(2, doc.Blocks.Count);
    ParagraphBlock first = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
    Assert.Equal("line one\nline two", Assert.IsType<TextSpan>(Assert.Single(first.Inlines)).Text);
  }

  [Fact]
  public void Document_With_Mixed_Structures_Parses_In_Order()
  {
    MarkdownDocument doc = MarkdownParser.Parse("## Plan\n\nSome **bold** text:\n\n```json\n{\"a\": 1}\n```\n- item A\n- item B\n\ndone.");
    Assert.Equal(5, doc.Blocks.Count);
    _ = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
    _ = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
    _ = Assert.IsType<CodeBlock>(doc.Blocks[2]);
    _ = Assert.IsType<ListBlock>(doc.Blocks[3]);
    _ = Assert.IsType<ParagraphBlock>(doc.Blocks[4]);
  }

  [Fact]
  public void Pipe_Table_With_Header_Parses_To_TableBlock()
  {
    MarkdownDocument doc = MarkdownParser.Parse("Name | Age\n--- | ---\nAlice | 30\nBob | 25");
    TableBlock table = Assert.IsType<TableBlock>(Assert.Single(doc.Blocks));
    Assert.Equal(2, table.Header.Cells.Count);
    Assert.Equal("Name", Assert.IsType<TextSpan>(Assert.Single(table.Header.Cells[0].Inlines)).Text);
    Assert.Equal("Age", Assert.IsType<TextSpan>(Assert.Single(table.Header.Cells[1].Inlines)).Text);
    Assert.Equal(2, table.Rows.Count);
    Assert.Equal("Alice", Assert.IsType<TextSpan>(Assert.Single(table.Rows[0].Cells[0].Inlines)).Text);
    Assert.Equal("30", Assert.IsType<TextSpan>(Assert.Single(table.Rows[0].Cells[1].Inlines)).Text);
    Assert.Equal("Bob", Assert.IsType<TextSpan>(Assert.Single(table.Rows[1].Cells[0].Inlines)).Text);
    Assert.Equal("25", Assert.IsType<TextSpan>(Assert.Single(table.Rows[1].Cells[1].Inlines)).Text);
  }

  [Fact]
  public void Table_Without_Delimiter_Row_Stays_A_Paragraph()
  {
    MarkdownDocument doc = MarkdownParser.Parse("a | b\nc | d");
    ParagraphBlock p = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
    Assert.Contains("|", Assert.IsType<TextSpan>(Assert.Single(p.Inlines)).Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Table_Edge_Pipes_And_Alignment_Colons_Tolerated()
  {
    MarkdownDocument doc = MarkdownParser.Parse("| Left | Center | Right |\n| :--- | :---: | ---: |\n| a | b | c |");
    TableBlock table = Assert.IsType<TableBlock>(Assert.Single(doc.Blocks));
    Assert.Equal(3, table.Header.Cells.Count);
    TableRow row = Assert.Single(table.Rows);
    Assert.Equal("a", Assert.IsType<TextSpan>(Assert.Single(row.Cells[0].Inlines)).Text);
    Assert.Equal("c", Assert.IsType<TextSpan>(Assert.Single(row.Cells[2].Inlines)).Text);
  }

  [Fact]
  public void Table_Cells_Parse_Inline_Formats()
  {
    MarkdownDocument doc = MarkdownParser.Parse("Cmd | Notes\n--- | ---\n`git` | **fast**");
    TableBlock table = Assert.IsType<TableBlock>(Assert.Single(doc.Blocks));
    Assert.Equal("git", Assert.IsType<CodeSpan>(Assert.Single(table.Rows[0].Cells[0].Inlines)).Code);
    BoldSpan bold = Assert.IsType<BoldSpan>(Assert.Single(table.Rows[0].Cells[1].Inlines));
    Assert.Equal("fast", Assert.IsType<TextSpan>(Assert.Single(bold.Children)).Text);
  }

  [Fact]
  public void Pipe_Inside_CodeSpan_Does_Not_Split_Cell()
  {
    MarkdownDocument doc = MarkdownParser.Parse("Expr | Result\n--- | ---\n`a | b` | x");
    TableBlock table = Assert.IsType<TableBlock>(Assert.Single(doc.Blocks));
    Assert.Equal("a | b", Assert.IsType<CodeSpan>(Assert.Single(table.Rows[0].Cells[0].Inlines)).Code);
    Assert.Equal("x", Assert.IsType<TextSpan>(Assert.Single(table.Rows[0].Cells[1].Inlines)).Text);
  }

  [Fact]
  public void Table_Sits_Between_Paragraphs_In_Order()
  {
    MarkdownDocument doc = MarkdownParser.Parse("before\n\nA | B\n--- | ---\n1 | 2\n\nafter");
    Assert.Equal(3, doc.Blocks.Count);
    _ = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
    _ = Assert.IsType<TableBlock>(doc.Blocks[1]);
    _ = Assert.IsType<ParagraphBlock>(doc.Blocks[2]);
  }
}

using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>Golden-span tests pinning MarkdownCodeTokenizer output: whole-text
///     tokenization (comments/strings spanning newlines stay colored), ordered
///     non-overlapping spans, and a plain-text fallback for unknown languages.</summary>
public class MarkdownCodeTokenizerTests
{
  [Fact]
  public void CSharp_Line_Comment_Is_Tokenized()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("// hi\nint x = 1;", "csharp");
    // the newline is layout: it stays a Default span between comment and keyword;
    // identifier x is its own Default span
    Assert.Equal(8, tokens.Length);
    Assert.Equal(MarkdownCodeTokenKind.Comment, tokens[0].Kind);
    Assert.Equal("// hi", tokens[0].Text);
    Assert.Equal("\n", tokens[1].Text);
    Assert.Equal(MarkdownCodeTokenKind.Keyword, tokens[2].Kind);
    Assert.Equal("int", tokens[2].Text);
    Assert.Equal("x", tokens[4].Text);
    Assert.Equal(MarkdownCodeTokenKind.Number, tokens[6].Kind);
    Assert.Equal("1", tokens[6].Text);
    Assert.Equal(";", tokens[7].Text);
  }

  [Fact]
  public void CSharp_Block_Comment_Spans_Newlines()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("a /* x\ny */ b", "csharp");
    MarkdownCodeToken comment = Assert.Single(tokens, t => t.Kind == MarkdownCodeTokenKind.Comment);
    Assert.Equal("/* x\ny */", comment.Text);
  }

  [Fact]
  public void CSharp_String_With_Escaped_Quote_Leaves_Number_Inside_Alone()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("x = \"a\\\"b\"; 7", "csharp");
    MarkdownCodeToken str = Assert.Single(tokens, t => t.Kind == MarkdownCodeTokenKind.String);
    Assert.Equal("\"a\\\"b\"", str.Text);
    Assert.Equal("7", Assert.Single(tokens, t => t.Kind == MarkdownCodeTokenKind.Number).Text);
  }
  [Fact]
  public void CSharp_Verbatim_String_Keeps_Entire_Literal()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("var s = @\"a\nb\";", "csharp");
    MarkdownCodeToken str = Assert.Single(tokens, t => t.Kind == MarkdownCodeTokenKind.String);
    Assert.Equal("@\"a\nb\"", str.Text);
  }

  [Fact]
  public void Xml_Tag_Attributes_And_Comments_Are_Tokenized()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("<!-- c --><Grid Row=\"1\"/>", "xml");
    Assert.Equal(8, tokens.Length);
    Assert.Equal((MarkdownCodeTokenKind.Comment, "<!-- c -->"), (tokens[0].Kind, tokens[0].Text));
    Assert.Equal((MarkdownCodeTokenKind.Keyword, "<"), (tokens[1].Kind, tokens[1].Text));
    Assert.Equal((MarkdownCodeTokenKind.Keyword, "Grid"), (tokens[2].Kind, tokens[2].Text));
    Assert.Equal((MarkdownCodeTokenKind.Keyword, "Row"), (tokens[4].Kind, tokens[4].Text));
    Assert.Equal((MarkdownCodeTokenKind.String, "\"1\""), (tokens[6].Kind, tokens[6].Text));
    Assert.Equal((MarkdownCodeTokenKind.Keyword, "/>"), (tokens[7].Kind, tokens[7].Text));
  }

  [Fact]
  public void Bash_Comment_And_Keyword_Are_Tokenized()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("# go\nfi", "bash");
    Assert.Equal(3, tokens.Length);
    Assert.Equal(MarkdownCodeTokenKind.Comment, tokens[0].Kind);
    Assert.Equal(MarkdownCodeTokenKind.Keyword, tokens[2].Kind);
    Assert.Equal("fi", tokens[2].Text);
  }

  [Fact]
  public void Python_Comment_Hash_String_Number()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("# c\nx = \"s\" 2", "python");
    Assert.Equal(MarkdownCodeTokenKind.Comment, tokens[0].Kind);
    Assert.Equal("\"s\"", Assert.Single(tokens, t => t.Kind == MarkdownCodeTokenKind.String).Text);
    Assert.Equal("2", Assert.Single(tokens, t => t.Kind == MarkdownCodeTokenKind.Number).Text);
  }

  [Fact]
  public void Json_Keys_Numbers_Literals()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("{\"k\": 1.5, \"b\": true}", "json");
    Assert.Equal(9, tokens.Length);
    Assert.Equal("{", tokens[0].Text);
    Assert.Equal(MarkdownCodeTokenKind.String, tokens[1].Kind);
    Assert.Equal(": ", tokens[2].Text);
    Assert.Equal(MarkdownCodeTokenKind.Number, tokens[3].Kind);
    Assert.Equal(MarkdownCodeTokenKind.String, tokens[5].Kind);
    Assert.Equal(MarkdownCodeTokenKind.Keyword, tokens[7].Kind);
    Assert.Equal("true", tokens[7].Text);
    Assert.Equal("}", tokens[8].Text);
  }

  [Fact]
  public void Unknown_Language_Yields_Single_Default_Span()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("any thing $x", "kotlin");
    MarkdownCodeToken token = Assert.Single(tokens);
    Assert.Equal(MarkdownCodeTokenKind.Default, token.Kind);
    Assert.Equal("any thing $x", token.Text);
  }

  [Fact]
  public void Empty_Language_Yields_Single_Default_Span()
  {
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize("let x = 1", "");
    MarkdownCodeToken token = Assert.Single(tokens);
    Assert.Equal(MarkdownCodeTokenKind.Default, token.Kind);
  }

  [Fact]
  public void Whole_Text_Is_Covered_Exactly_Once_In_Order()
  {
    const string code = "int x = 2; // t\n/* m */ y = \"s\"";
    MarkdownCodeToken[] tokens = MarkdownCodeTokenizer.Tokenize(code, "csharp");
    Assert.Equal(code, string.Concat(tokens.Select(t => t.Text)));
  }
}

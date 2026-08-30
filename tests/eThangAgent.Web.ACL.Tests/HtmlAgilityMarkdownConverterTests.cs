namespace eThangAgent.Web.ACL.Tests;

/// <summary>Converter behavior over fixture HTML — the readable-text 90%: structure,
///     links, emphasis, code, lists, tables. Malformed input must never throw.</summary>
public class HtmlAgilityMarkdownConverterTests
{
  private static string Md(string html) => new HtmlAgilityMarkdownConverter().Convert(html, new Uri("https://example.com/page"));

  // ---- Structure ----

  [Fact]
  public void Heading_MapsToHashes() => Assert.Equal("# Title", Md("<html><body><h1>Title</h1></body></html>"));

  [Fact]
  public void Paragraphs_SeparatedByBlankLine() => Assert.Equal("One\n\nTwo", Md("<p>One</p><p>Two</p>"));

  [Fact]
  public void Bold_And_Italic_And_Code_Inline()
  {
    Assert.Equal("a **bold** b", Md("<p>a <b>bold</b> b</p>"));
    Assert.Equal("an *it* i", Md("<p>an <i>it</i> i</p>"));
    Assert.Equal("some `x = 1` code", Md("<p>some <code>x = 1</code> code</p>"));
  }

  [Fact]
  public void Script_Style_Noscript_Dropped() => Assert.Equal("", Md("<script>var x = 1;</script><style>p { }</style>"));

  [Fact]
  public void Empty_And_Malformed_NeverThrow()
  {
    Assert.Equal("", Md(""));
    Assert.Equal("", Md("   "));
    // Unclosed bold tag: HAP auto-closes it — best effort is bold text, not emptiness.
    Assert.Equal("Unclosed **tags**", Md("<p>Unclosed <b>tags"));
  }

  // ---- Links ----

  [Fact]
  public void Link_IsEmittedWithAbsoluteUrl() => Assert.Equal("[Docs](https://example.com/docs)", Md("<a href=\"/docs\">Docs</a>"));

  [Fact]
  public void AnchorWithoutHref_RendersAsText() => Assert.Equal("Named anchor", Md("<a name=\"x\">Named anchor</a>"));

  [Fact]
  public void Image_EmitsAltAndAbsoluteSrc() => Assert.Equal("![logo](https://example.com/img.png)", Md("<img src=\"https://example.com/img.png\" alt=\"logo\">"));

  // ---- Lists ----

  [Fact]
  public void UnorderedList_EmitsDashes() => Assert.Equal("- a\n- b", Md("<ul><li>a</li><li>b</li></ul>"));

  [Fact]
  public void OrderedList_EmitsNumbers() => Assert.Equal("1. a\n2. b", Md("<ol><li>a</li><li>b</li></ol>"));

  // ---- Code blocks ----

  [Fact]
  public void PreCode_EmitsFence_WithLanguageWhenClassPresent()
  {
    string html = "<pre><code class=\"language-csharp\">var x = 1;</code></pre>";
    string expected = "```csharp\nvar x = 1;\n```";
    Assert.Equal(expected, Md(html));
  }

  // ---- Tables ----

  [Fact]
  public void Table_EmitsGfmTable()
  {
    string html = "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>";
    const string nl = "\n";
    string expected = "| A | B |" + nl + "| --- | --- |" + nl + "| 1 | 2 |";
    Assert.Equal(expected, Md(html));
  }

  // ---- Noise ----

  [Fact]
  public void Nav_Aside_Footer_ContentExcluded() => Assert.Equal("Keep", Md("<nav>Menu</nav><p>Keep</p><footer>Bye</footer>"));
}

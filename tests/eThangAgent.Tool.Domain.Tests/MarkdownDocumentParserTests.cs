using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Strict-boundary tests for MarkdownDocumentParser: unknown block types,
/// wrong field types, out-of-range levels, ragged tables, and bad frontmatter values are
/// all rejected with Error codes - nothing silently coerced or defaulted.</summary>
public class MarkdownDocumentParserTests
{
  private static Result<MarkdownDocument> Parse(string documentJson) =>
      MarkdownDocumentParser.Parse(JsonDocument.Parse(documentJson).RootElement);

  private const string TextDoc = /*lang=json,strict*/ """{"blocks":[{"type":"text","text":"Hi"}]}""";

  // ---- happy paths ----

  [Fact]
  public void Minimal_TextDocument_Parses()
  {
    Result<MarkdownDocument> parsed = Parse(TextDoc);
    Assert.True(parsed.IsSuccess);
    TextBlock block = Assert.IsType<TextBlock>(Assert.Single(parsed.Value.Blocks));
    Assert.Equal("Hi", block.Text);
  }

  [Fact]
  public void Every_BlockType_Parses()
  {
    string json = /*lang=json,strict*/ """
        {"blocks":[
            {"type":"header","level":2,"text":"T"},
            {"type":"quote","text":"q"},
            {"type":"alert","alertType":"WARNING","text":"w"},
            {"type":"codeBlock","language":"csharp","code":"var x=1;"},
            {"type":"unorderedList","items":[{"text":"a"},{"text":"b","children":[{"text":"c"}]}]},
            {"type":"numberedList","items":[{"text":"n"}]},
            {"type":"taskList","items":[{"label":"t","isComplete":true}]},
            {"type":"table","headers":[{"text":"H","align":"left"},"P"],"rows":[["1","2"]]},
            {"type":"space"},
            {"type":"space","count":3},
            null
        ]}
        """;
    Result<MarkdownDocument> parsed = Parse(json);
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    IReadOnlyList<MarkdownBlock?> blocks = parsed.Value.Blocks;
    Assert.Equal(11, blocks.Count);
    Assert.Null(blocks[^1]); // trailing null entry preserved for renderer to skip
    _ = Assert.IsType<HeaderBlock>(blocks[0]);
    _ = Assert.IsType<QuoteBlock>(blocks[1]);
    AlertBlock alert = Assert.IsType<AlertBlock>(blocks[2]);
    Assert.Equal(AlertType.Warning, alert.Alert);
    CodeBlock code = Assert.IsType<CodeBlock>(blocks[3]);
    Assert.Equal("csharp", code.Language);
    ListBlock ul = Assert.IsType<ListBlock>(blocks[4]);
    Assert.Equal(ListKind.Unordered, ul.Kind);
    Assert.Equal("c", Assert.Single(ul.Items[1].Children!).Text);
    Assert.Equal(ListKind.Numbered, Assert.IsType<ListBlock>(blocks[5]).Kind);
    TaskListBlock tl = Assert.IsType<TaskListBlock>(blocks[6]);
    Assert.True(tl.Items[0].IsComplete);
    TableBlock table = Assert.IsType<TableBlock>(blocks[7]);
    Assert.Equal(TableAlign.Left, table.Headers[0].Align);
    Assert.Null(table.Headers[1].Align);
    Assert.Equal(1, Assert.IsType<SpaceBlock>(blocks[8]).Count);
    Assert.Equal(3, Assert.IsType<SpaceBlock>(blocks[9]).Count);
  }

  [Fact]
  public void FrontMatter_Parses_MixedScalarTypes()
  {
    string json = /*lang=json,strict*/ """{"frontmatter":{"title":"T","weight":80,"ok":true,"name":"x"},"blocks":[]}""";
    Result<MarkdownDocument> parsed = Parse(json);
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    IReadOnlyDictionary<string, object> fm = parsed.Value.FrontMatter!;
    Assert.Equal(80.0, (double)fm["weight"]);
    Assert.True((bool)fm["ok"]);
    Assert.Equal("T", fm["title"]);
  }

  // ---- rejections ----

  [Fact]
  public void Unknown_BlockType_Rejected()
  {
    Result<MarkdownDocument> parsed = Parse(/*lang=json,strict*/ """{"blocks":[{"type":"marquee","text":"x"}]}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("UnknownParameter", parsed.Error.Code);
    Assert.Contains("marquee", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Header_LevelZero_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"header","level":0,"text":"T"}]}""");

  [Fact]
  public void Header_LevelFour_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"header","level":4,"text":"T"}]}""");

  [Fact]
  public void Header_LevelAsString_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"header","level":"2","text":"T"}]}""");

  [Fact]
  public void Text_NonStringText_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"text","text":42}]}""");

  [Fact]
  public void Alert_UnknownVariant_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"alert","alertType":"HOTFIX","text":"x"}]}""");

  [Fact]
  public void CodeBlock_MissingCode_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"codeBlock"}]}""");

  [Fact]
  public void List_ItemMissingText_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"unorderedList","items":[{"label":"a"}]}]}""");

  [Fact]
  public void TaskList_MissingLabel_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"taskList","items":[{"isComplete":true}]}]}""");

  [Fact]
  public void Table_RowLengthMismatch_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"table","headers":["A","B"],"rows":[["1","2"],["3"]]}]}""");

  [Fact]
  public void Table_EmptyHeaders_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"table","headers":[],"rows":[]}]}""");

  [Fact]
  public void Space_CountBelowOne_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"space","count":0}]}""");

  [Fact]
  public void Block_MissingType_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"text":"x"}]}""");

  [Fact]
  public void Block_ExtraField_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":[{"type":"text","text":"x","color":"red"}]}""");

  [Fact]
  public void FrontMatter_ObjectValue_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"frontmatter":{"k":{"nested":1}},"blocks":[]}""");

  [Fact]
  public void FrontMatter_NullValue_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"frontmatter":{"k":null},"blocks":[]}""");

  [Fact]
  public void FrontMatter_NewlineValue_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"frontmatter":{"k":"line1\nline2"},"blocks":[]}""");

  [Fact]
  public void FrontMatter_NotAnObject_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"frontmatter":[1],"blocks":[]}""");

  [Fact]
  public void Blocks_NotAnArray_Rejected() =>
      Rejects(/*lang=json,strict*/ """{"blocks":"text"}""");

  [Fact]
  public void Blocks_Missing_Rejected() =>
      Rejects("{}");

  private static void Rejects(string json)
  {
    Result<MarkdownDocument> parsed = Parse(json);
    Assert.False(parsed.IsSuccess, "expected rejection for: " + json);
    Assert.NotNull(parsed.Error);
  }
}

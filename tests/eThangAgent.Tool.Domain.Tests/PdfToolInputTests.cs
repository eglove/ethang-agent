using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Strict input parsing for the pdf tool (task 2): a required action switch
///     (exactly one of read, render, create, manipulate, case-sensitive), per-action
///     required and optional keys, unknown keys rejected, page-range strings parsed
///     through <see cref="PdfPageRange.Parse"/> at input time (bad grammar refused here,
///     never at execution), expectedVersion decoded through
///     <see cref="FileVersionToken.Decode"/>, and every range checked with NO clamping.</summary>
public class PdfToolInputTests
{
  // JSON002 fires only in the format/IDE host; the pragma pair mirrors the repo's
  // standing pattern for literal JSON arguments in fixtures.
#pragma warning disable JSON002

  private const string VersionArg = "\"expectedVersion\":\"v1:638000000000000000\"";

  private static Result<PdfToolInput> Parse(string json) => PdfToolInput.FromArguments(json);

  private static PdfToolInput ParseOk(string json)
  {
    Result<PdfToolInput> parsed = Parse(json);
    Assert.True(parsed.IsSuccess, "parse failed: " + (parsed.Error?.Code ?? "?") + ": " + (parsed.Error?.Message ?? ""));
    return parsed.Value;
  }

  private static string ErrorCode(string json)
  {
    Result<PdfToolInput> parsed = Parse(json);
    Assert.False(parsed.IsSuccess, "expected failure but parse succeeded");
    return parsed.Error.Code;
  }

  private static string ErrorMessage(string json)
  {
    Result<PdfToolInput> parsed = Parse(json);
    Assert.False(parsed.IsSuccess, "expected failure but parse succeeded");
    return parsed.Error.Message;
  }

  // ---- happy paths ----

  [Fact]
  public void Read_ParsesWithDefaults()
  {
    PdfToolInput input = ParseOk("{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"doc.pdf\"}");
    Assert.Multiple(
        () => Assert.Equal("read", input.Action),
        () => Assert.Equal("doc.pdf", input.Path),
        () => Assert.Equal(0, input.Offset),
        () => Assert.Equal(20, input.MaxPages),
        () => Assert.Null(input.Pages),
        () => Assert.Null(input.ExpectedVersion));
  }

  [Fact]
  public void Read_ParsesOffsetAndMaxPages()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"doc.pdf\",\"offset\":5,\"maxPages\":50}");
    Assert.Multiple(
        () => Assert.Equal(5, input.Offset),
        () => Assert.Equal(50, input.MaxPages));
  }

  [Fact]
  public void Render_ParsesPagesAndDpi()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"doc.pdf\",\"pages\":\"1-3,5\",\"dpi\":300}");
    Assert.Multiple(
        () => Assert.Equal("render", input.Action),
        () => Assert.Equal("doc.pdf", input.Path),
        () => Assert.Equal([1, 2, 3, 5], input.Pages),
        () => Assert.Equal(300, input.Dpi));
  }

  [Fact]
  public void Render_ParsesWithDefaultDpi()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"doc.pdf\",\"pages\":\"2\"}");
    Assert.Multiple(
        () => Assert.Equal([2], input.Pages),
        () => Assert.Equal(150, input.Dpi));
  }

  [Fact]
  public void Create_ParsesMarkdown()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"create\",\"path\":\"new.pdf\",\"markdown\":\"# Title\"}");
    Assert.Multiple(
        () => Assert.Equal("create", input.Action),
        () => Assert.Equal("# Title", input.Markdown),
        () => Assert.Null(input.ExpectedVersion));
  }

  [Fact]
  public void Manipulate_ParsesRotate()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"1-3\",\"degrees\":90}]}");
    RotatePages op = Assert.IsType<RotatePages>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal([1, 2, 3], op.Pages),
        () => Assert.Equal(90, op.Degrees),
        () => Assert.Equal(1L, input.ExpectedVersion!.Length));
  }

  [Fact]
  public void Manipulate_ParsesRotate_BarePage()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"3\",\"degrees\":180}]}");
    RotatePages op = Assert.IsType<RotatePages>(input.Operations.Single());
    Assert.Equal([3], op.Pages);
  }

  [Fact]
  public void Manipulate_ParsesDeletePages()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"deletePages\",\"pages\":\"2,4-5\"}]}");
    DeletePages op = Assert.IsType<DeletePages>(input.Operations.Single());
    Assert.Equal([2, 4, 5], op.Pages);
  }

  [Fact]
  public void Manipulate_ParsesReorderPages()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"reorderPages\",\"order\":[3,1,2]}]}");
    ReorderPages op = Assert.IsType<ReorderPages>(input.Operations.Single());
    Assert.Equal([3, 1, 2], op.Order);
  }

  [Fact]
  public void Manipulate_ParsesExtractPages()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"extractPages\",\"pages\":\"1-2\",\"outputPath\":\"out.pdf\"}]}");
    ExtractPages op = Assert.IsType<ExtractPages>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal([1, 2], op.Pages),
        () => Assert.Equal("out.pdf", op.OutputPath));
  }

  [Fact]
  public void Manipulate_ParsesMerge()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"merge\",\"sources\":[\"a.pdf\",\"b.pdf\"]}]}");
    Merge op = Assert.IsType<Merge>(input.Operations.Single());
    Assert.Equal(["a.pdf", "b.pdf"], op.Sources);
  }

  [Fact]
  public void Manipulate_ParsesWatermarkText_WithOptionals()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"CONFIDENTIAL\",\"fontSize\":24.0," +
        "\"opacity\":0.5,\"degrees\":45}]}");
    WatermarkText op = Assert.IsType<WatermarkText>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("CONFIDENTIAL", op.Text),
        () => Assert.Equal(24.0, op.FontSize),
        () => Assert.Equal(0.5, op.Opacity),
        () => Assert.Equal(45, op.Degrees));
  }

  [Fact]
  public void Manipulate_ParsesWatermarkText_WithoutOptionals()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"DRAFT\"}]}");
    WatermarkText op = Assert.IsType<WatermarkText>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("DRAFT", op.Text),
        () => Assert.Null(op.FontSize),
        () => Assert.Null(op.Opacity),
        () => Assert.Null(op.Degrees));
  }

  [Fact]
  public void Manipulate_ParsesSetMetadata()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"setMetadata\",\"title\":\"T\",\"author\":\"A\",\"subject\":\"S\"}]}");
    SetMetadata op = Assert.IsType<SetMetadata>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("T", op.Title),
        () => Assert.Equal("A", op.Author),
        () => Assert.Equal("S", op.Subject));
  }

  [Fact]
  public void Manipulate_ParsesSetMetadata_TitleOnly()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"setMetadata\",\"title\":\"T\"}]}");
    SetMetadata op = Assert.IsType<SetMetadata>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("T", op.Title),
        () => Assert.Null(op.Author),
        () => Assert.Null(op.Subject));
  }

  [Fact]
  public void Manipulate_ParsesMultipleOperations()
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"1\",\"degrees\":90}," +
        "{\"op\":\"deletePages\",\"pages\":\"2\"},{\"op\":\"merge\",\"sources\":[\"b.pdf\"]}]}");
    Assert.Equal(3, input.Operations.Count);
    _ = Assert.IsType<RotatePages>(input.Operations[0]);
    _ = Assert.IsType<DeletePages>(input.Operations[1]);
    _ = Assert.IsType<Merge>(input.Operations[2]);
  }

  // ---- action switch ----

  [Fact]
  public void MissingAction_Fails_ListingValidActions()
  {
    string json = "{\"timeoutSeconds\":120,\"path\":\"a.pdf\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("read", ErrorMessage(json), StringComparison.Ordinal),
        () => Assert.Contains("manipulate", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void UnknownAction_Fails_ListingValidActions()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"upsert\",\"path\":\"a.pdf\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("upsert", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void ActionWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType,
        ErrorCode("{\"timeoutSeconds\":120,\"action\":7,\"path\":\"a.pdf\"}"));
  }

  [Fact]
  public void MissingPath_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"read\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("path", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void EmptyPath_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue,
        ErrorCode("{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"\"}"));
  }

  [Fact]
  public void UnknownParameter_Fails_NamingTheKey()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.pdf\",\"width\":100}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(json)),
        () => Assert.Contains("width", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Read_RejectsRenderOnlyKeys()
  {
    Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.pdf\",\"dpi\":150}"));
  }

  // ---- read ranges: no clamping ----

  [Fact]
  public void Read_NegativeOffset_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.pdf\",\"offset\":-1}"));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(101)]
  public void Read_MaxPagesOutOfRange_Fails(int maxPages)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.pdf\",\"maxPages\":" + maxPages + "}"));
  }

  [Theory]
  [InlineData(1)]
  [InlineData(100)]
  public void Read_MaxPagesBoundaries_AreAccepted(int maxPages)
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.pdf\",\"maxPages\":" + maxPages + "}");
    Assert.Equal(maxPages, input.MaxPages);
  }

  // ---- render ----

  [Fact]
  public void Render_MissingPages_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"a.pdf\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("pages", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Render_BadPagesGrammar_FailsWithInvalidPageRange()
  {
    Assert.Equal(PdfErrorCodes.InvalidPageRange, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"a.pdf\",\"pages\":\"x\"}"));
  }

  [Fact]
  public void Render_OpenTail_FailsWithInvalidPageRange()
  {
    Assert.Equal(PdfErrorCodes.InvalidPageRange, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"a.pdf\",\"pages\":\"8-\"}"));
  }

  [Fact]
  public void Render_PagesWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"a.pdf\",\"pages\":5}"));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(301)]
  public void Render_DpiOutOfRange_Fails(int dpi)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"a.pdf\",\"pages\":\"1\",\"dpi\":" + dpi + "}"));
  }

  [Theory]
  [InlineData(1)]
  [InlineData(300)]
  public void Render_DpiBoundaries_AreAccepted(int dpi)
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"render\",\"path\":\"a.pdf\",\"pages\":\"1\",\"dpi\":" + dpi + "}");
    Assert.Equal(dpi, input.Dpi);
  }

  // ---- create ----

  [Fact]
  public void Create_MissingMarkdown_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"create\",\"path\":\"new.pdf\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("markdown", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Create_EmptyMarkdown_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"create\",\"path\":\"new.pdf\",\"markdown\":\"\"}"));
  }

  [Fact]
  public void Create_MarkdownWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"create\",\"path\":\"new.pdf\",\"markdown\":5}"));
  }

  // ---- manipulate: expectedVersion and operations array ----

  [Fact]
  public void Manipulate_MissingExpectedVersion_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," +
        "\"operations\":[{\"op\":\"deletePages\",\"pages\":\"1\"}]}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("expectedVersion", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Manipulate_GarbageExpectedVersion_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\",\"expectedVersion\":\"vabc\"" +
        ",\"operations\":[{\"op\":\"deletePages\",\"pages\":\"1\"}]}"));
  }

  [Fact]
  public void Manipulate_MissingOperations_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg + "}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("operations", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Manipulate_EmptyOperations_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[]}"));
  }

  [Fact]
  public void Manipulate_OperationsWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":5}"));
  }

  [Fact]
  public void Manipulate_OperationElementNotObject_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[5]}"));
  }

  [Fact]
  public void Manipulate_OperationMissingOpDiscriminator_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"pages\":\"1\"}]}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("op", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Manipulate_OpWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":7}]}"));
  }

  [Fact]
  public void Manipulate_UnknownOp_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"teleport\",\"pages\":\"1\"}]}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("teleport", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Manipulate_OperationUnknownKey_Fails()
  {
    Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"deletePages\",\"pages\":\"1\",\"degrees\":90}]}"));
  }

  [Fact]
  public void Manipulate_FiftyOperations_AreAccepted()
  {
    string ops = string.Join(",", Enumerable.Repeat("{\"op\":\"deletePages\",\"pages\":\"1\"}", 50));
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[" + ops + "]}");
    Assert.Equal(50, input.Operations.Count);
  }

  [Fact]
  public void Manipulate_MoreThanFiftyOperations_Fail()
  {
    string ops = string.Join(",", Enumerable.Repeat("{\"op\":\"deletePages\",\"pages\":\"1\"}", 51));
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[" + ops + "]}"));
  }

  // ---- rotate ----

  [Fact]
  public void Rotate_MissingPages_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"degrees\":90}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("rotate", message, StringComparison.Ordinal),
        () => Assert.Contains("pages", message, StringComparison.Ordinal));
  }

  [Fact]
  public void Rotate_BadPagesGrammar_FailsWithInvalidPageRange()
  {
    Assert.Equal(PdfErrorCodes.InvalidPageRange, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"5-2\",\"degrees\":90}]}"));
  }

  [Fact]
  public void Rotate_OpenTail_FailsWithInvalidPageRange()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"8-\",\"degrees\":90}]}";
    Assert.Multiple(
        () => Assert.Equal(PdfErrorCodes.InvalidPageRange, ErrorCode(json)),
        () => Assert.Contains("8-", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Rotate_MissingDegrees_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"1\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("degrees", message, StringComparison.Ordinal));
  }

  [Theory]
  [InlineData(45)]
  [InlineData(0)]
  [InlineData(360)]
  [InlineData(-90)]
  public void Rotate_DegreesNotMultipleOf90_Fails(int degrees)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"1\",\"degrees\":" + degrees + "}]}"));
  }

  [Theory]
  [InlineData(90)]
  [InlineData(180)]
  [InlineData(270)]
  public void Rotate_DegreesBoundaries_AreAccepted(int degrees)
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"1\",\"degrees\":" + degrees + "}]}");
    RotatePages op = Assert.IsType<RotatePages>(input.Operations.Single());
    Assert.Equal(degrees, op.Degrees);
  }

  [Fact]
  public void Rotate_DegreesWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"rotate\",\"pages\":\"1\",\"degrees\":\"90\"}]}"));
  }

  // ---- deletePages ----

  [Fact]
  public void DeletePages_MissingPages_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"deletePages\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("deletePages", message, StringComparison.Ordinal),
        () => Assert.Contains("pages", message, StringComparison.Ordinal));
  }

  // ---- reorderPages ----

  [Fact]
  public void ReorderPages_MissingOrder_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"reorderPages\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("order", message, StringComparison.Ordinal));
  }

  [Fact]
  public void ReorderPages_EmptyOrder_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"reorderPages\",\"order\":[]}]}"));
  }

  [Fact]
  public void ReorderPages_OrderWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"reorderPages\",\"order\":5}]}"));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void ReorderPages_OrderEntryBelowOne_Fails(int entry)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"reorderPages\",\"order\":[2," + entry + "]}]}"));
  }

  [Fact]
  public void ReorderPages_NonIntEntry_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"reorderPages\",\"order\":[1,\"2\"]}]}"));
  }

  // ---- extractPages ----

  [Fact]
  public void ExtractPages_MissingOutputPath_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"extractPages\",\"pages\":\"1\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("outputPath", message, StringComparison.Ordinal));
  }

  [Fact]
  public void ExtractPages_EmptyOutputPath_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"extractPages\",\"pages\":\"1\",\"outputPath\":\"\"}]}"));
  }

  // ---- merge ----

  [Fact]
  public void Merge_MissingSources_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"merge\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("sources", message, StringComparison.Ordinal));
  }

  [Fact]
  public void Merge_EmptySources_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"merge\",\"sources\":[]}]}"));
  }

  [Fact]
  public void Merge_NonStringSource_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"merge\",\"sources\":[5]}]}"));
  }

  [Fact]
  public void Merge_EmptySourceEntry_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"merge\",\"sources\":[\"a.pdf\",\"\"]}]}"));
  }

  // ---- watermarkText ----

  [Fact]
  public void Watermark_MissingText_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("text", message, StringComparison.Ordinal));
  }

  [Fact]
  public void Watermark_EmptyText_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"\"}]}"));
  }

  [Theory]
  [InlineData(0.0)]
  [InlineData(-1.0)]
  public void Watermark_FontSizeNonPositive_Fails(double fontSize)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"X\",\"fontSize\":" + fontSize + "}]}"));
  }

  [Theory]
  [InlineData(1.5)]
  [InlineData(-0.1)]
  public void Watermark_OpacityOutOfRange_Fails(double opacity)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"X\",\"opacity\":" + opacity + "}]}"));
  }

  [Theory]
  [InlineData(0.0)]
  [InlineData(1.0)]
  public void Watermark_OpacityBoundaries_AreAccepted(double opacity)
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"X\",\"opacity\":" + opacity + "}]}");
    WatermarkText op = Assert.IsType<WatermarkText>(input.Operations.Single());
    Assert.Equal(opacity, op.Opacity);
  }

  [Theory]
  [InlineData(181)]
  [InlineData(-181)]
  public void Watermark_DegreesOutOfRange_Fails(int degrees)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"X\",\"degrees\":" + degrees + "}]}"));
  }

  [Theory]
  [InlineData(-180)]
  [InlineData(180)]
  public void Watermark_DegreesBoundaries_AreAccepted(int degrees)
  {
    PdfToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"X\",\"degrees\":" + degrees + "}]}");
    WatermarkText op = Assert.IsType<WatermarkText>(input.Operations.Single());
    Assert.Equal(degrees, op.Degrees);
  }

  [Fact]
  public void Watermark_OpacityWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"watermarkText\",\"text\":\"X\",\"opacity\":\"0.5\"}]}"));
  }

  // ---- setMetadata ----

  [Fact]
  public void SetMetadata_NoFields_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"setMetadata\"}]}";
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json));
  }

  [Fact]
  public void SetMetadata_NonStringField_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"manipulate\",\"path\":\"a.pdf\"," + VersionArg +
        ",\"operations\":[{\"op\":\"setMetadata\",\"title\":5}]}"));
  }

#pragma warning restore JSON002
}

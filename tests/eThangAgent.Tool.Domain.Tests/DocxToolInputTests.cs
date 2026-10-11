using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Strict input parsing for the docx tool (task 2): a required action switch
///     (exactly one of read, edit, create, comments, revisions, extract, case-sensitive),
///     per-action required and optional keys, unknown keys rejected, address fields
///     parsed through <see cref="DocxAddress.Parse"/> at input time, expectedVersion
///     decoded through <see cref="FileVersionToken.Decode"/>, and every range checked
///     with NO clamping.</summary>
public class DocxToolInputTests
{
  // JSON002 fires only in the format/IDE host; the pragma pair mirrors the repo's
  // standing pattern for literal JSON arguments in fixtures.
#pragma warning disable JSON002

  private const string VersionArg = "\"expectedVersion\":\"v1:638000000000000000\"";

  private static Result<DocxToolInput> Parse(string json) => DocxToolInput.FromArguments(json);

  private static DocxToolInput ParseOk(string json)
  {
    Result<DocxToolInput> parsed = Parse(json);
    Assert.True(parsed.IsSuccess, "parse failed: " + (parsed.Error?.Code ?? "?") + ": " + (parsed.Error?.Message ?? ""));
    return parsed.Value;
  }

  private static string ErrorCode(string json)
  {
    Result<DocxToolInput> parsed = Parse(json);
    Assert.False(parsed.IsSuccess, "expected failure but parse succeeded");
    return parsed.Error.Code;
  }

  private static string ErrorMessage(string json)
  {
    Result<DocxToolInput> parsed = Parse(json);
    Assert.False(parsed.IsSuccess, "expected failure but parse succeeded");
    return parsed.Error.Message;
  }

  // ---- happy paths ----

  [Fact]
  public void Read_ParsesWithDefaults()
  {
    DocxToolInput input = ParseOk("{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"report.docx\"}");
    Assert.Multiple(
        () => Assert.Equal("read", input.Action),
        () => Assert.Equal("report.docx", input.Path),
        () => Assert.Equal(0, input.Offset),
        () => Assert.Equal(200, input.MaxBlocks),
        () => Assert.Null(input.ExpectedVersion));
  }

  [Fact]
  public void Read_ParsesOffsetAndMaxBlocks()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"report.docx\",\"offset\":5,\"maxBlocks\":50}");
    Assert.Multiple(
        () => Assert.Equal(5, input.Offset),
        () => Assert.Equal(50, input.MaxBlocks));
  }

  [Fact]
  public void Edit_ParsesReplaceParagraphText()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"replaceParagraphText\",\"address\":\"p1\",\"text\":\"Hello\"}]}");
    ReplaceParagraphText op = Assert.IsType<ReplaceParagraphText>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("p1", op.Address),
        () => Assert.Equal("Hello", op.Text),
        () => Assert.Equal(1L, input.ExpectedVersion!.Length));
  }

  [Fact]
  public void Edit_ParsesSetParagraphStyle()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"setParagraphStyle\",\"address\":\"p2\",\"style\":\"Heading 1\"}]}");
    SetParagraphStyle op = Assert.IsType<SetParagraphStyle>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("p2", op.Address),
        () => Assert.Equal("Heading 1", op.StyleName));
  }

  [Fact]
  public void Edit_ParsesInsertParagraph_WithAndWithoutStyle()
  {
    DocxToolInput withStyle = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"insertParagraph\",\"afterAddress\":\"p1\",\"text\":\"New\",\"style\":\"Quote\"}]}");
    InsertParagraph styled = Assert.IsType<InsertParagraph>(withStyle.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("p1", styled.AfterAddress),
        () => Assert.Equal("New", styled.Text),
        () => Assert.Equal("Quote", styled.Style));

    DocxToolInput withoutStyle = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"insertParagraph\",\"afterAddress\":\"p0\",\"text\":\"New\"}]}");
    InsertParagraph bare = Assert.IsType<InsertParagraph>(withoutStyle.Operations.Single());
    Assert.Null(bare.Style);
  }

  [Fact]
  public void Edit_ParsesDeleteParagraph()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"deleteParagraph\",\"address\":\"p3\"}]}");
    DeleteParagraph op = Assert.IsType<DeleteParagraph>(input.Operations.Single());
    Assert.Equal("p3", op.Address);
  }

  [Fact]
  public void Edit_ParsesSetCellText()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"setCellText\",\"address\":\"t2.r1.c3\",\"text\":\"cell value\"}]}");
    SetCellText op = Assert.IsType<SetCellText>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("t2.r1.c3", op.Address),
        () => Assert.Equal("cell value", op.Text));
  }

  [Fact]
  public void Edit_ParsesInsertTableRow()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"insertTableRow\",\"tableAddress\":\"t2\",\"afterRow\":2}]}");
    InsertTableRow op = Assert.IsType<InsertTableRow>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("t2", op.TableAddress),
        () => Assert.Equal(2, op.AfterRowIndex));
  }

  [Fact]
  public void Edit_ParsesDeleteTableRow()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"deleteTableRow\",\"tableAddress\":\"t2\",\"row\":3}]}");
    DeleteTableRow op = Assert.IsType<DeleteTableRow>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("t2", op.TableAddress),
        () => Assert.Equal(3, op.RowIndex));
  }

  [Fact]
  public void Edit_ParsesApplyRunFormatting()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"applyRunFormatting\",\"address\":\"p4\"," +
        "\"format\":{\"bold\":true,\"colorHex\":\"FF0000\"}}]}");
    ApplyRunFormatting op = Assert.IsType<ApplyRunFormatting>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("p4", op.Address),
        () => Assert.True(op.Format.Bold!.Value),
        () => Assert.Equal("FF0000", op.Format.ColorHex),
        () => Assert.Null(op.Format.Italic));
  }

  [Fact]
  public void Edit_ParsesFindReplace_AllowsEmptyReplace()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"findReplace\",\"find\":\"foo\",\"replace\":\"\"}]}");
    FindReplace op = Assert.IsType<FindReplace>(input.Operations.Single());
    Assert.Multiple(
        () => Assert.Equal("foo", op.Find),
        () => Assert.Equal("", op.Replace));
  }

  [Fact]
  public void Create_ParsesMarkdown()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"create\",\"path\":\"new.docx\",\"markdown\":\"# Title\"}");
    Assert.Multiple(
        () => Assert.Equal("create", input.Action),
        () => Assert.Equal("# Title", input.Markdown),
        () => Assert.Null(input.ExpectedVersion));
  }

  [Fact]
  public void Comments_List_Parses()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"list\"}");
    Assert.Multiple(
        () => Assert.Equal("comments", input.Action),
        () => Assert.Equal("list", input.CommentAction),
        () => Assert.Null(input.ExpectedVersion));
  }

  [Fact]
  public void Comments_Add_Parses()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"add\"," +
        "\"address\":\"p3\",\"author\":\"Ethan\",\"text\":\"Check this\"," + VersionArg + "}");
    Assert.Multiple(
        () => Assert.Equal("add", input.CommentAction),
        () => Assert.Equal("p3", input.Address),
        () => Assert.Equal("Ethan", input.Author),
        () => Assert.Equal("Check this", input.Text),
        () => Assert.Equal(1L, input.ExpectedVersion!.Length));
  }

  [Fact]
  public void Comments_Delete_Parses()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"delete\"," +
        "\"commentId\":7," + VersionArg + "}");
    Assert.Multiple(
        () => Assert.Equal("delete", input.CommentAction),
        () => Assert.Equal(7, input.CommentId),
        () => Assert.NotNull(input.ExpectedVersion));
  }

  [Fact]
  public void Revisions_List_Parses()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"list\"}");
    Assert.Multiple(
        () => Assert.Equal("revisions", input.Action),
        () => Assert.Equal("list", input.RevisionAction),
        () => Assert.Null(input.Scope));
  }

  [Fact]
  public void Revisions_Accept_DefaultScopeIsAll()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        VersionArg + "}");
    DocxRevisionScope scope = input.Scope ?? throw new InvalidOperationException("expected a scope");
    Assert.Multiple(
        () => Assert.Equal("accept", input.RevisionAction),
        () => Assert.True(scope.All),
        () => Assert.Null(scope.Author),
        () => Assert.Null(scope.Address));
  }

  [Fact]
  public void Revisions_Accept_ParsesAuthorScope()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"author\":\"Ethan\"}," + VersionArg + "}");
    DocxRevisionScope scope = input.Scope ?? throw new InvalidOperationException("expected a scope");
    Assert.Multiple(
        () => Assert.False(scope.All),
        () => Assert.Equal("Ethan", scope.Author),
        () => Assert.Null(scope.Address));
  }

  [Fact]
  public void Revisions_Accept_ParsesAddressScope()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"reject\"," +
        "\"scope\":{\"address\":\"p2\"}," + VersionArg + "}");
    DocxRevisionScope scope = input.Scope ?? throw new InvalidOperationException("expected a scope");
    Assert.Multiple(
        () => Assert.False(scope.All),
        () => Assert.Null(scope.Author),
        () => Assert.Equal("p2", scope.Address));
  }

  [Fact]
  public void Extract_ParsesNames()
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"extract\",\"path\":\"a.docx\"," +
        "\"names\":[\"image1.png\",\"image2.jpeg\"]}");
    Assert.Equal(["image1.png", "image2.jpeg"], input.ImageNames);
  }

  [Fact]
  public void Extract_WithoutNames_ImageNamesIsNull()
  {
    DocxToolInput input = ParseOk("{\"timeoutSeconds\":120,\"action\":\"extract\",\"path\":\"a.docx\"}");
    Assert.Null(input.ImageNames);
  }

  // ---- action switch ----

  [Fact]
  public void MissingAction_Fails_ListingValidActions()
  {
    string json = "{\"timeoutSeconds\":120,\"path\":\"a.docx\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("read", ErrorMessage(json), StringComparison.Ordinal),
        () => Assert.Contains("extract", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void UnknownAction_Fails_ListingValidActions()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"upsert\",\"path\":\"a.docx\"}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("upsert", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void ActionWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType,
        ErrorCode("{\"timeoutSeconds\":120,\"action\":7,\"path\":\"a.docx\"}"));
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
    string json = "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.docx\",\"width\":100}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(json)),
        () => Assert.Contains("width", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Read_RejectsEditOnlyKeys()
  {
    Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.docx\",\"operations\":[]}"));
  }

  // ---- read ranges: no clamping ----

  [Fact]
  public void Read_NegativeOffset_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.docx\",\"offset\":-1}"));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(401)]
  public void Read_MaxBlocksOutOfRange_Fails(int maxBlocks)
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.docx\",\"maxBlocks\":" + maxBlocks + "}"));
  }

  [Theory]
  [InlineData(1)]
  [InlineData(400)]
  public void Read_MaxBlocksBoundaries_AreAccepted(int maxBlocks)
  {
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"read\",\"path\":\"a.docx\",\"maxBlocks\":" + maxBlocks + "}");
    Assert.Equal(maxBlocks, input.MaxBlocks);
  }

  // ---- edit: operations array ----

  [Fact]
  public void Edit_MissingOperations_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg + "}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("operations", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_EmptyOperations_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[]}"));
  }

  [Fact]
  public void Edit_OperationsWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":5}"));
  }

  [Fact]
  public void Edit_OperationElementNotObject_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[5]}"));
  }

  [Fact]
  public void Edit_OperationMissingOpDiscriminator_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"address\":\"p1\"}]}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("op", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_UnknownOp_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"teleport\",\"address\":\"p1\"}]}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("teleport", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_MissingField_Fails_NamingOpAndField()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"replaceParagraphText\",\"address\":\"p1\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("replaceParagraphText", message, StringComparison.Ordinal),
        () => Assert.Contains("text", message, StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_FieldWrongType_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"replaceParagraphText\",\"address\":\"p1\",\"text\":5}]}"));
  }

  [Fact]
  public void Edit_FiftyOperations_AreAccepted()
  {
    string ops = string.Join(",", Enumerable.Repeat("{\"op\":\"deleteParagraph\",\"address\":\"p1\"}", 50));
    DocxToolInput input = ParseOk(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[" + ops + "]}");
    Assert.Equal(50, input.Operations.Count);
  }

  [Fact]
  public void Edit_MoreThanFiftyOperations_Fail()
  {
    string ops = string.Join(",", Enumerable.Repeat("{\"op\":\"deleteParagraph\",\"address\":\"p1\"}", 51));
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[" + ops + "]}"));
  }

  [Fact]
  public void Edit_BadAddress_FailsWithInvalidAddress()
  {
    Assert.Equal(DocxErrorCodes.InvalidAddress, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"replaceParagraphText\",\"address\":\"px\",\"text\":\"x\"}]}"));
  }

  [Fact]
  public void Edit_MissingExpectedVersion_FailsWithMissingParameter()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," +
        "\"operations\":[{\"op\":\"deleteParagraph\",\"address\":\"p1\"}]}";
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(json)),
        () => Assert.Contains("expectedVersion", ErrorMessage(json), StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_GarbageExpectedVersion_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\",\"expectedVersion\":\"vabc\"" +
        ",\"operations\":[{\"op\":\"deleteParagraph\",\"address\":\"p1\"}]}"));
  }

  // ---- edit: per-op field rules ----

  [Fact]
  public void Edit_InsertParagraph_MissingAfterAddress_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"insertParagraph\",\"text\":\"New\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("afterAddress", message, StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_ApplyRunFormatting_EmptyFormat_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"applyRunFormatting\",\"address\":\"p1\",\"format\":{}}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("format", message, StringComparison.Ordinal));
  }

  [Fact]
  public void Edit_ApplyRunFormatting_MissingFormat_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"applyRunFormatting\",\"address\":\"p1\"}]}";
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json));
  }

  [Fact]
  public void Edit_ApplyRunFormatting_UnknownFormatKey_Fails()
  {
    Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"applyRunFormatting\",\"address\":\"p1\"," +
        "\"format\":{\"bold\":true,\"strikethrough\":true}}]}"));
  }

  [Fact]
  public void Edit_ApplyRunFormatting_BadColorHex_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"applyRunFormatting\",\"address\":\"p1\"," +
        "\"format\":{\"colorHex\":\"red\"}}]}"));
  }

  [Fact]
  public void Edit_FindReplace_EmptyFind_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"findReplace\",\"find\":\"\",\"replace\":\"x\"}]}"));
  }

  [Fact]
  public void Edit_FindReplace_MissingReplace_Fails()
  {
    string json = "{\"timeoutSeconds\":120,\"action\":\"edit\",\"path\":\"a.docx\"," + VersionArg +
        ",\"operations\":[{\"op\":\"findReplace\",\"find\":\"foo\"}]}";
    string message = ErrorMessage(json);
    Assert.Multiple(
        () => Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(json)),
        () => Assert.Contains("replace", message, StringComparison.Ordinal));
  }

  // ---- comments ----

  [Fact]
  public void Comments_MissingCommentAction_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\"}"));
  }

  [Fact]
  public void Comments_UnknownCommentAction_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"edit\"}"));
  }

  [Fact]
  public void Comments_Add_MissingAddress_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"add\"," +
        "\"author\":\"Ethan\",\"text\":\"t\"," + VersionArg + "}"));
  }

  [Fact]
  public void Comments_Add_EmptyAuthor_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"add\"," +
        "\"address\":\"p1\",\"author\":\"\",\"text\":\"t\"," + VersionArg + "}"));
  }

  [Fact]
  public void Comments_Add_EmptyText_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"add\"," +
        "\"address\":\"p1\",\"author\":\"Ethan\",\"text\":\"\"," + VersionArg + "}"));
  }

  [Fact]
  public void Comments_Add_BadAddress_FailsWithInvalidAddress()
  {
    Assert.Equal(DocxErrorCodes.InvalidAddress, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"add\"," +
        "\"address\":\"q9\",\"author\":\"Ethan\",\"text\":\"t\"," + VersionArg + "}"));
  }

  [Fact]
  public void Comments_Add_MissingExpectedVersion_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"add\"," +
        "\"address\":\"p1\",\"author\":\"Ethan\",\"text\":\"t\"}"));
  }

  [Fact]
  public void Comments_Delete_MissingCommentId_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"delete\"," +
        VersionArg + "}"));
  }

  [Fact]
  public void Comments_Delete_ZeroCommentId_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"delete\"," +
        "\"commentId\":0," + VersionArg + "}"));
  }

  [Fact]
  public void Comments_Delete_MissingExpectedVersion_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"comments\",\"path\":\"a.docx\",\"commentAction\":\"delete\"," +
        "\"commentId\":3}"));
  }

  // ---- revisions ----

  [Fact]
  public void Revisions_MissingRevisionAction_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\"}"));
  }

  [Fact]
  public void Revisions_UnknownRevisionAction_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"merge\"}"));
  }

  [Fact]
  public void Revisions_Accept_MissingExpectedVersion_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"}"));
  }

  [Fact]
  public void Revisions_Reject_MissingExpectedVersion_FailsWithMissingParameter()
  {
    Assert.Equal(ToolErrorCodes.MissingParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"reject\"}"));
  }

  [Fact]
  public void Revisions_Scope_AllAndAuthor_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"all\":true,\"author\":\"Ethan\"}," + VersionArg + "}"));
  }

  [Fact]
  public void Revisions_Scope_AuthorAndAddress_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"author\":\"Ethan\",\"address\":\"p1\"}," + VersionArg + "}"));
  }

  [Fact]
  public void Revisions_Scope_AllFalseAlone_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"all\":false}," + VersionArg + "}"));
  }

  [Fact]
  public void Revisions_Scope_NotObject_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":5," + VersionArg + "}"));
  }

  [Fact]
  public void Revisions_Scope_UnknownKey_Fails()
  {
    Assert.Equal(ToolErrorCodes.UnknownParameter, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"author\":\"Ethan\",\"note\":\"x\"}," + VersionArg + "}"));
  }

  [Fact]
  public void Revisions_Scope_BadAddress_FailsWithInvalidAddress()
  {
    Assert.Equal(DocxErrorCodes.InvalidAddress, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"address\":\"zz\"}," + VersionArg + "}"));
  }

  [Fact]
  public void Revisions_Scope_EmptyAuthor_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"revisions\",\"path\":\"a.docx\",\"revisionAction\":\"accept\"," +
        "\"scope\":{\"author\":\"\"}," + VersionArg + "}"));
  }

  // ---- extract ----

  [Fact]
  public void Extract_EmptyName_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"extract\",\"path\":\"a.docx\",\"names\":[\"a.png\",\"\"]}"));
  }

  [Fact]
  public void Extract_NonStringName_Fails()
  {
    Assert.Equal(ToolErrorCodes.InvalidParameterType, ErrorCode(
        "{\"timeoutSeconds\":120,\"action\":\"extract\",\"path\":\"a.docx\",\"names\":[5]}"));
  }

#pragma warning restore JSON002
}

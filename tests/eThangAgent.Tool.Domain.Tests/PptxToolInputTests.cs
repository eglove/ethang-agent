using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Strict input-parsing tests for the pptx tool: the five-action switch, the
///     optional slide, the required version token on every mutating action, per-action key
///     rejection, and every operation object parsed into its typed record - with bad
///     grammar, unknown op names, and unknown operation keys refused at input time.</summary>
public sealed class PptxToolInputTests
{
#pragma warning disable JSON002

  private const string Token = "v100:638000000000000000";

  private static Result<PptxToolInput> Parse(string json) => PptxToolInput.FromArguments(json);

  private static string EditJson(string operations) =>
      "{\"action\":\"edit\",\"path\":\"deck.pptx\",\"expectedVersion\":\"" + Token + "\",\"operations\":" + operations + "}";

  // ---- action switch ----

  [Fact]
  public void Action_UnknownIsRefusedNamingAllFive()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"slides\",\"path\":\"deck.pptx\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
    Assert.Contains("read, edit, create, comments, extract", r.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("read", "{\"action\":\"read\",\"path\":\"deck.pptx\"}")]
  [InlineData("edit", "{\"action\":\"edit\",\"path\":\"deck.pptx\",\"expectedVersion\":\"v100:638000000000000000\",\"operations\":[{\"op\":\"deleteSlide\",\"slide\":1}]}")]
  [InlineData("create", "{\"action\":\"create\",\"path\":\"deck.pptx\",\"markdown\":\"# T\"}")]
  [InlineData("comments", "{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"list\"}")]
  [InlineData("extract", "{\"action\":\"extract\",\"path\":\"deck.pptx\"}")]
  public void Action_EveryAdvertisedActionIsAccepted(string action, string json)
  {
    Result<PptxToolInput> r = Parse(json);
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    Assert.Equal(action, r.Value.Action);
  }

  [Fact]
  public void Action_AbsentIsRefused()
  {
    Result<PptxToolInput> r = Parse("{\"path\":\"deck.pptx\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Path_AbsentIsMissingParameter()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.MissingParameter, r.Error.Code);
  }

  [Fact]
  public void Path_EmptyIsRefused()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\",\"path\":\"\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
    Assert.Contains("non-empty", r.Error.Message, StringComparison.Ordinal);
  }

  // ---- read ----

  [Fact]
  public void Read_WithoutSlideIsTheOutline()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\",\"path\":\"deck.pptx\"}");
    Assert.True(r.IsSuccess);
    Assert.Equal(0, r.Value.Slide);
  }

  [Fact]
  public void Read_WithSlideCarriesIt()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\",\"path\":\"deck.pptx\",\"slide\":3}");
    Assert.True(r.IsSuccess);
    Assert.Equal(3, r.Value.Slide);
  }

  [Fact]
  public void Read_SlideZeroIsRefused()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\",\"path\":\"deck.pptx\",\"slide\":0}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
    Assert.Contains("'slide'", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Read_OffsetIsNotAParameter()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\",\"path\":\"deck.pptx\",\"offset\":5}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.UnknownParameter, r.Error.Code);
  }

  [Fact]
  public void Read_MaxBlocksIsNotAParameter()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"read\",\"path\":\"deck.pptx\",\"maxBlocks\":5}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.UnknownParameter, r.Error.Code);
  }

  // ---- edit ----

  [Fact]
  public void Edit_WithoutExpectedVersionIsRefusedNamingIt()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"edit\",\"path\":\"deck.pptx\",\"operations\":[{\"op\":\"deleteSlide\",\"slide\":1}]}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.MissingParameter, r.Error.Code);
    Assert.Contains("expectedVersion", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Edit_OperationsNotAnArrayIsRefused()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"edit\",\"path\":\"deck.pptx\",\"expectedVersion\":\"" + Token + "\",\"operations\":\"nope\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterType, r.Error.Code);
    Assert.Contains("array of operation objects", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Edit_EmptyOperationsIsRefused()
  {
    Result<PptxToolInput> r = Parse(EditJson("[]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Edit_UnknownOpIsRefusedNamingTheVocabulary()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"setBackground\",\"slide\":1}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
    Assert.Contains("replaceText", r.Error.Message, StringComparison.Ordinal);
    Assert.Contains("removeAnimation", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Edit_UnknownOperationKeyIsRefused()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"deleteSlide\",\"slide\":1,\"bogus\":2}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.UnknownParameter, r.Error.Code);
  }

  [Fact]
  public void Edit_RefusalNamesTheOperationPosition()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"deleteSlide\",\"slide\":1},{\"op\":\"deleteSlide\",\"slide\":0}]"));
    Assert.False(r.IsSuccess);
    Assert.Contains("operations[1]", r.Error.Message, StringComparison.Ordinal);
  }

  // ---- operation vocabulary ----

  [Fact]
  public void Operation_ReplaceTextCarriesAddressAndText()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"replaceText\",\"address\":\"s2.sh1\",\"text\":\"Hello\"}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    ReplaceText op = Assert.IsType<ReplaceText>(Assert.Single(r.Value.Operations));
    Assert.Equal("s2.sh1", op.Address);
    Assert.Equal("Hello", op.Text);
  }

  [Fact]
  public void Operation_ReplaceTextAdmitsATableCell()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"replaceText\",\"address\":\"s2.sh3.t1.c4\",\"text\":\"x\"}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    Assert.Equal("s2.sh3.t1.c4", Assert.IsType<ReplaceText>(Assert.Single(r.Value.Operations)).Address);
  }

  [Fact]
  public void Operation_ReplaceTextRefusesASlideAddress()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"replaceText\",\"address\":\"s2\",\"text\":\"x\"}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, r.Error.Code);
  }

  [Fact]
  public void Operation_ReplaceTextRefusesBadGrammar()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"replaceText\",\"address\":\"slide2\",\"text\":\"x\"}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, r.Error.Code);
  }

  [Fact]
  public void Operation_SetNotesCarriesSlideAndText()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"setNotes\",\"slide\":2,\"text\":\"note\"}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    SetNotes op = Assert.IsType<SetNotes>(Assert.Single(r.Value.Operations));
    Assert.Equal(2, op.Slide);
    Assert.Equal("note", op.Text);
  }

  [Fact]
  public void Operation_InsertSlideAdmitsZeroAfterSlide()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"insertSlide\",\"afterSlide\":0,\"layout\":\"Title and Content\"}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    InsertSlide op = Assert.IsType<InsertSlide>(Assert.Single(r.Value.Operations));
    Assert.Equal(0, op.AfterSlide);
    Assert.Equal("Title and Content", op.LayoutName);
  }

  [Fact]
  public void Operation_InsertSlideRefusesNegativeAfterSlide()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"insertSlide\",\"afterSlide\":-1,\"layout\":\"Blank\"}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Operation_MoveSlideCarriesFromAndTo()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"moveSlide\",\"from\":1,\"to\":3}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    MoveSlide op = Assert.IsType<MoveSlide>(Assert.Single(r.Value.Operations));
    Assert.Equal(1, op.From);
    Assert.Equal(3, op.To);
  }

  [Fact]
  public void Operation_SetSlideLayoutCarriesSlideAndLayout()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"setSlideLayout\",\"slide\":4,\"layout\":\"Blank\"}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    SetSlideLayout op = Assert.IsType<SetSlideLayout>(Assert.Single(r.Value.Operations));
    Assert.Equal(4, op.Slide);
    Assert.Equal("Blank", op.LayoutName);
  }

  [Fact]
  public void Operation_ReplacePictureRefusesACellAddress()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"replacePicture\",\"address\":\"s1.sh2.t1.c1\",\"image\":\"a.png\"}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, r.Error.Code);
  }

  [Fact]
  public void Operation_InsertPictureCarriesGeometry()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"insertPicture\",\"slide\":1,\"image\":\"a.png\",\"x\":914400,\"y\":914400,\"width\":1828800,\"height\":1371600}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    InsertPicture op = Assert.IsType<InsertPicture>(Assert.Single(r.Value.Operations));
    Assert.Equal(914400, op.X);
    Assert.Equal(1828800, op.Width);
    Assert.Equal(1371600, op.Height);
  }

  [Fact]
  public void Operation_InsertPictureRefusesZeroWidth()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"insertPicture\",\"slide\":1,\"image\":\"a.png\",\"x\":0,\"y\":0,\"width\":0,\"height\":10}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
    Assert.Contains("'width'", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Operation_InsertPictureRefusesNegativeOffset()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"insertPicture\",\"slide\":1,\"image\":\"a.png\",\"x\":-1,\"y\":0,\"width\":10,\"height\":10}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Operation_InsertTableCarriesTheCellGrid()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"insertTable\",\"slide\":1,\"rows\":2,\"cols\":2,\"cells\":[[\"a\",\"b\"],[\"c\",\"d\"]],\"x\":0,\"y\":0,\"width\":100,\"height\":100}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    InsertTable op = Assert.IsType<InsertTable>(Assert.Single(r.Value.Operations));
    Assert.Equal(2, op.Rows);
    Assert.Equal("d", op.Cells[1][1]);
  }

  [Fact]
  public void Operation_InsertTableRefusesACellCountMismatch()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"insertTable\",\"slide\":1,\"rows\":2,\"cols\":3,\"cells\":[[\"a\",\"b\"]],\"x\":0,\"y\":0,\"width\":100,\"height\":100}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, r.Error.Code);
    Assert.Contains("6", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Operation_InsertTextBoxCarriesTextAndGeometry()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"insertTextBox\",\"slide\":1,\"text\":\"hi\",\"x\":0,\"y\":0,\"width\":100,\"height\":100}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    InsertTextBox op = Assert.IsType<InsertTextBox>(Assert.Single(r.Value.Operations));
    Assert.Equal("hi", op.Text);
  }

  [Fact]
  public void Operation_SetChartDataCarriesValues()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"setChartData\",\"address\":\"s1.sh2\",\"series\":[\"Revenue\"],\"values\":[[1,2,3]]}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    SetChartData op = Assert.IsType<SetChartData>(Assert.Single(r.Value.Operations));
    Assert.Equal("Revenue", Assert.Single(op.SeriesNames!));
    Assert.Equal(3, Assert.Single(op.Values!).Count);
  }

  [Fact]
  public void Operation_SetChartDataRequiresAtLeastOneMember()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"setChartData\",\"address\":\"s1.sh2\"}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Operation_SetChartDataRefusesNonNumericValues()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"setChartData\",\"address\":\"s1.sh2\",\"values\":[[\"a\"]]}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterType, r.Error.Code);
  }

  [Fact]
  public void Operation_SetAnimationCarriesItsMembers()
  {
    Result<PptxToolInput> r = Parse(EditJson(
        "[{\"op\":\"setAnimation\",\"slide\":1,\"effectIndex\":1,\"effect\":\"fade\",\"trigger\":\"onClick\",\"durationMilliseconds\":2000}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    SetAnimation op = Assert.IsType<SetAnimation>(Assert.Single(r.Value.Operations));
    Assert.Equal("fade", op.Effect);
    Assert.Equal("onClick", op.Trigger);
    Assert.Equal(2000, op.DurationMilliseconds);
  }

  [Fact]
  public void Operation_SetAnimationLeavesAbsentMembersNull()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"setAnimation\",\"slide\":1,\"effectIndex\":1,\"durationMilliseconds\":500}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    SetAnimation op = Assert.IsType<SetAnimation>(Assert.Single(r.Value.Operations));
    Assert.Null(op.Effect);
    Assert.Null(op.Trigger);
  }

  [Fact]
  public void Operation_SetAnimationRefusesNegativeDuration()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"setAnimation\",\"slide\":1,\"effectIndex\":1,\"durationMilliseconds\":-5}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Operation_RemoveAnimationCarriesSlideAndIndex()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"removeAnimation\",\"slide\":2,\"effectIndex\":2}]"));
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    RemoveAnimation op = Assert.IsType<RemoveAnimation>(Assert.Single(r.Value.Operations));
    Assert.Equal(2, op.EffectIndex);
  }

  [Fact]
  public void Operation_RemoveAnimationRefusesIndexZero()
  {
    Result<PptxToolInput> r = Parse(EditJson("[{\"op\":\"removeAnimation\",\"slide\":2,\"effectIndex\":0}]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Operation_MoreThanFiftyIsRefused()
  {
    string ops = string.Join(",", Enumerable.Range(0, 51).Select(_ => "{\"op\":\"deleteSlide\",\"slide\":1}"));
    Result<PptxToolInput> r = Parse(EditJson("[" + ops + "]"));
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  // ---- create ----

  [Fact]
  public void Create_RequiresMarkdown()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"create\",\"path\":\"new.pptx\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.MissingParameter, r.Error.Code);
    Assert.Contains("markdown", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_CarriesTheSource()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"create\",\"path\":\"new.pptx\",\"markdown\":\"# Title\"}");
    Assert.True(r.IsSuccess);
    Assert.Equal("# Title", r.Value.Markdown);
  }

  // ---- comments ----

  [Fact]
  public void Comments_ListNeedsNoVersion()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"list\"}");
    Assert.True(r.IsSuccess);
    Assert.Null(r.Value.ExpectedVersion);
  }

  [Fact]
  public void Comments_AddRequiresVersionAddressAuthorAndText()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"add\",\"address\":\"s1.sh1\",\"author\":\"A\",\"text\":\"T\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.MissingParameter, r.Error.Code);
    Assert.Contains("expectedVersion", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Comments_AddCarriesItsMembers()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"add\"," +
        "\"address\":\"s2.sh1\",\"author\":\"Reviewer\",\"text\":\"Check this figure\",\"expectedVersion\":\"" + Token + "\"}");
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    Assert.Equal("s2.sh1", r.Value.Address);
    Assert.Equal("Reviewer", r.Value.Author);
  }

  [Fact]
  public void Comments_AddRefusesASlideAnchor()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"add\"," +
        "\"address\":\"s2\",\"author\":\"A\",\"text\":\"T\",\"expectedVersion\":\"" + Token + "\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, r.Error.Code);
  }

  [Fact]
  public void Comments_AddRefusesEmptyAuthor()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"add\"," +
        "\"address\":\"s2.sh1\",\"author\":\"\",\"text\":\"T\",\"expectedVersion\":\"" + Token + "\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Comments_DeleteRequiresAVersion()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"delete\",\"commentId\":1}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.MissingParameter, r.Error.Code);
  }

  [Fact]
  public void Comments_DeleteCarriesTheId()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"delete\"," +
        "\"commentId\":3,\"expectedVersion\":\"" + Token + "\"}");
    Assert.True(r.IsSuccess, r.IsSuccess ? string.Empty : r.Error.Message);
    Assert.Equal(3, r.Value.CommentId);
  }

  [Fact]
  public void Comments_DeleteRefusesIdZero()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"delete\"," +
        "\"commentId\":0,\"expectedVersion\":\"" + Token + "\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Comments_UnknownCommentActionIsRefused()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"comments\",\"path\":\"deck.pptx\",\"commentAction\":\"resolve\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  // ---- extract ----

  [Fact]
  public void Extract_CarriesTheNameFilter()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"extract\",\"path\":\"deck.pptx\",\"names\":[\"/ppt/media/image1.png\"]}");
    Assert.True(r.IsSuccess);
    Assert.Equal("/ppt/media/image1.png", Assert.Single(r.Value.ImageNames!));
  }

  [Fact]
  public void Extract_RefusesAnEmptyName()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"extract\",\"path\":\"deck.pptx\",\"names\":[\"\"]}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Extract_NeedsNoVersion()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"extract\",\"path\":\"deck.pptx\"}");
    Assert.True(r.IsSuccess);
    Assert.Null(r.Value.ImageNames);
  }

  // ---- version token ----

  [Fact]
  public void ExpectedVersion_GarbageIsRefused()
  {
    Result<PptxToolInput> r = Parse("{\"action\":\"edit\",\"path\":\"deck.pptx\",\"expectedVersion\":\"nope\",\"operations\":[{\"op\":\"deleteSlide\",\"slide\":1}]}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }
}

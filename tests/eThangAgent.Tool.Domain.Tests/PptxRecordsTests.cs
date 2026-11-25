namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the pptx records: each constructor validates its own
///     invariants and throws for programmer error only, the five shape subtypes are the
///     whole shape vocabulary, and the operation vocabulary carries exactly the members
///     the tool parses model input into.</summary>
public class PptxRecordsTests
{
  private static readonly FileVersion Version = new(1024, new DateTime(638000000000000000, DateTimeKind.Utc));

  // ---- PptxShapeInfo subtypes: a negative index is programmer error ----

  [Fact]
  public void TextBoxShape_NegativeIndex_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => new PptxTextBoxShape(-1, "s1.sh1", "text", isTitle: false, isPlaceholder: false));

  [Fact]
  public void PictureShape_NegativeIndex_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => new PptxPictureShape(-1, "s1.sh1", "media1.png"));

  [Fact]
  public void TableShape_NegativeIndex_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => new PptxTableShape(-1, "s1.sh1", 1, 1, [new PptxCellText(1, 1, "x")]));

  [Fact]
  public void ChartShape_NegativeIndex_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => new PptxChartShape(-1, "s1.sh1", "bar", [], []));

  [Fact]
  public void GroupShape_NegativeIndex_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => new PptxGroupShape(-1, "s1.sh1", []));

  [Fact]
  public void Shape_EmptyAddress_Throws() =>
      _ = Assert.Throws<ArgumentException>(
          () => new PptxTextBoxShape(1, "", "text", isTitle: false, isPlaceholder: false));

  [Fact]
  public void Shape_EachSubtypeReportsItsKind()
  {
    Assert.Multiple(
        () => Assert.Equal("text", new PptxTextBoxShape(1, "s1.sh1", "t", false, false).Kind),
        () => Assert.Equal("picture", new PptxPictureShape(1, "s1.sh1", "m.png").Kind),
        () => Assert.Equal("table", new PptxTableShape(1, "s1.sh1", 1, 1, [new PptxCellText(1, 1, "x")]).Kind),
        () => Assert.Equal("chart", new PptxChartShape(1, "s1.sh1", "bar", [], []).Kind),
        () => Assert.Equal("group", new PptxGroupShape(1, "s1.sh1", []).Kind));
  }

  [Fact]
  public void TableShape_CellCountMismatch_Throws() =>
      _ = Assert.Throws<ArgumentException>(
          () => new PptxTableShape(1, "s1.sh1", 2, 3, [new PptxCellText(1, 1, "x")]));

  [Fact]
  public void TableShape_CellCountMatchesDimensions_Constructs()
  {
    PptxTableShape table = new(1, "s1.sh1", 2, 2,
        [new PptxCellText(1, 1, "a"), new PptxCellText(1, 2, "b"),
         new PptxCellText(2, 1, "c"), new PptxCellText(2, 2, "d")]);
    Assert.Multiple(
        () => Assert.Equal(2, table.Rows),
        () => Assert.Equal(2, table.Cols),
        () => Assert.Equal(4, table.Cells.Count));
  }

  [Fact]
  public void CellText_RowBelowOne_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxCellText(0, 1, "x"));

  [Fact]
  public void CellText_ColBelowOne_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxCellText(1, 0, "x"));

  // ---- Outline and slide records ----

  [Fact]
  public void Outline_SlideCountDisagreesWithList_Throws() =>
      _ = Assert.Throws<ArgumentException>(
          () => new PptxOutline(Version, 2, [new PptxSlideSummary(1, "T", "F", false, "Title")], [], []));

  [Fact]
  public void Outline_CarriesVersionCountSlidesMediaAndLayouts()
  {
    PptxOutline outline = new(
        Version,
        1,
        [new PptxSlideSummary(1, "Title", "First", false, "Title and Content")],
        [new PptxMediaInfo("ppt/media/image1.png", "image/png", 4096)],
        ["Title", "Title and Content"]);
    Assert.Multiple(
        () => Assert.Equal(Version, outline.Version),
        () => Assert.Equal(1, outline.SlideCount),
        () => Assert.Single(outline.Slides),
        () => Assert.Single(outline.Media),
        () => Assert.Equal(2, outline.LayoutNames.Count));
  }

  [Fact]
  public void SlideSummary_NumberZero_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxSlideSummary(0, "T", "F", false, "Title"));

  [Fact]
  public void SlideSnapshot_CarriesTheSlideContentAndDeckFacts()
  {
    PptxSlideSnapshot snapshot = new(
        Version,
        new PptxSlideContent(2, "Title and Content", true, [new PptxTextBoxShape(1, "s2.sh1", "Hello", true, true)]),
        "Notes text",
        [new PptxMediaInfo("ppt/media/image1.png", "image/png", 10)],
        [new PptxCommentInfo(1, "Reviewer", "Check this", "s2.sh1", false)],
        [new PptxAnimationInfo(1, "s2.sh1", "fade", "on click", "500")]);
    Assert.Multiple(
        () => Assert.Equal(Version, snapshot.Version),
        () => Assert.Equal(2, snapshot.Number),
        () => Assert.Equal("Title and Content", snapshot.LayoutName),
        () => Assert.True(snapshot.Hidden),
        () => Assert.Single(snapshot.Shapes),
        () => Assert.Equal("Notes text", snapshot.Notes),
        () => Assert.Single(snapshot.Media),
        () => Assert.Single(snapshot.Comments),
        () => Assert.Single(snapshot.Animations));
  }

  [Fact]
  public void SlideContent_NumberZero_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxSlideContent(0, "Title", false, []));

  // ---- Media, comment, animation, outcome records ----

  [Fact]
  public void MediaInfo_EmptyName_Throws() =>
      _ = Assert.Throws<ArgumentException>(() => new PptxMediaInfo("", "image/png", 1));

  [Fact]
  public void MediaInfo_NegativeByteCount_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxMediaInfo("ppt/media/i.png", "image/png", -1));

  [Fact]
  public void CommentInfo_EmptyAnchorAddress_Throws() =>
      _ = Assert.Throws<ArgumentException>(() => new PptxCommentInfo(1, "a", "t", "", false));

  [Fact]
  public void CommentInfo_NegativeId_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxCommentInfo(-1, "a", "t", "s1.sh1", false));

  [Fact]
  public void AnimationInfo_IndexZero_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxAnimationInfo(0, "s1.sh1", "fade", "on click", "500"));

  [Fact]
  public void AnimationInfo_EmptyAddress_Throws() =>
      _ = Assert.Throws<ArgumentException>(() => new PptxAnimationInfo(1, "", "fade", "on click", "500"));

  [Fact]
  public void EditOutcome_NegativeChangedCount_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PptxEditOutcome(Version, -1));

  [Fact]
  public void SaveOutcome_CarriesTheNewVersion()
  {
    PptxSaveOutcome outcome = new(Version);
    Assert.Equal(Version, outcome.NewVersion);
  }

  // ---- The operation vocabulary ----

  [Fact]
  public void Operations_AreAllPptxOperations()
  {
    PptxOperation[] operations =
    [
        new ReplaceText("s1.sh1", "text"),
        new SetNotes(1, "notes"),
        new InsertSlide(0, "Title"),
        new DeleteSlide(1),
        new MoveSlide(1, 2),
        new SetSlideLayout(1, "Title"),
        new ReplacePicture("s1.sh1", "image.png"),
        new InsertPicture(1, "image.png", 0, 0, 100, 100),
        new InsertTable(1, 1, 1, [["cell"]], 0, 0, 100, 100),
        new InsertTextBox(1, "text", 0, 0, 100, 100),
        new SetChartData("s1.sh1", ["Series"], ["Category"], [[1.0]]),
        new SetAnimation(1, 1, "fade", null, 500),
        new RemoveAnimation(1, 1),
    ];
    Assert.All(operations, op => Assert.IsType<PptxOperation>(op, exactMatch: false));
  }

  [Fact]
  public void SetChartData_NullMembersAreLeftUnchanged()
  {
    SetChartData op = new("s1.sh1", null, null, null);
    Assert.Multiple(
        () => Assert.Null(op.SeriesNames),
        () => Assert.Null(op.CategoryNames),
        () => Assert.Null(op.Values));
  }

  [Fact]
  public void SetAnimation_NullMembersAreLeftUnchanged()
  {
    SetAnimation op = new(1, 2, null, null, null);
    Assert.Multiple(
        () => Assert.Null(op.Effect),
        () => Assert.Null(op.Trigger),
        () => Assert.Null(op.DurationMilliseconds));
  }

  // ---- Every error code is a stable, distinct string ----

  [Fact]
  public void ErrorCodes_AreDistinctAndNonEmpty()
  {
    string[] codes =
    [
        PptxErrorCodes.NotAPptx,
        PptxErrorCodes.MacroPresentation,
        PptxErrorCodes.CorruptPresentation,
        PptxErrorCodes.OversizePresentation,
        PptxErrorCodes.StalePresentation,
        PptxErrorCodes.InvalidAddress,
        PptxErrorCodes.AddressNotFound,
        PptxErrorCodes.SlideNotFound,
        PptxErrorCodes.LayoutNotFound,
        PptxErrorCodes.StrandedContent,
        PptxErrorCodes.ChartDataMissing,
        PptxErrorCodes.ChartTypeUnsupported,
        PptxErrorCodes.AnimationNotFound,
        PptxErrorCodes.CommentNotFound,
        PptxErrorCodes.MarkdownInvalid,
        PptxErrorCodes.ImageNotAttachable,
        PptxErrorCodes.OperationUnsupported,
        PptxErrorCodes.FileNotFound,
    ];
    Assert.Multiple(
        () => Assert.DoesNotContain(codes, c => string.IsNullOrEmpty(c)),
        () => Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count()));
  }
}

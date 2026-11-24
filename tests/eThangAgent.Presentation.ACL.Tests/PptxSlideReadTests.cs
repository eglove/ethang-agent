using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The slide read: shapes in document order with their addresses and kinds, text
///     with paragraphs and breaks preserved, tables, charts, pictures, groups, notes,
///     hidden slides, comments, animations, and the two refusals a bad slide number or a
///     bad shape address produces.</summary>
public sealed class PptxSlideReadTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
  {
    DirectoryInfo dir = TestTempRoot.CreateDirectory("pptx-slide");
    return Path.Combine(dir.FullName, name);
  }

  [Fact]
  public async Task Numbers_the_title_body_and_picture_as_three_shapes_in_document_order()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("three.pptx"),
        [new TestSlideSpec { Title = "Chapter 1", Body = "Covers batteries", WithPicture = true }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal(["s1.sh1", "s1.sh2", "s1.sh3"], slide.Shapes.Select(shape => shape.Address));
    Assert.Equal(["text", "text", "picture"], slide.Shapes.Select(shape => shape.Kind));
    PptxTextBoxShape title = Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]);
    Assert.Equal("Chapter 1", title.Text);
    Assert.True(title.IsTitle);
    Assert.True(title.IsPlaceholder);
    PptxPictureShape picture = Assert.IsType<PptxPictureShape>(slide.Shapes[2]);
    Assert.StartsWith("/ppt/media/", picture.MediaName, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Reports_a_body_placeholder_as_a_placeholder_that_is_not_a_title()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("body.pptx"),
        [new TestSlideSpec { Title = "T", Body = "B" }]);
    PptxTextBoxShape body = Assert.IsType<PptxTextBoxShape>((await ReadSlide(path, 1)).Shapes[1]);
    Assert.Equal("B", body.Text);
    Assert.False(body.IsTitle);
    Assert.True(body.IsPlaceholder);
  }

  [Fact]
  public async Task Joins_paragraphs_with_a_newline_and_keeps_a_break_inside_one_paragraph()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("breaks.pptx"),
        [new TestSlideSpec { Title = "T", Body = "one\ntwo", WithLineBreak = true }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal("one\ntwo", Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]).Text);
    Assert.Equal("First line\nSecond line", Assert.IsType<PptxTextBoxShape>(slide.Shapes[2]).Text);
  }

  [Fact]
  public async Task Concatenates_runs_and_ignores_empty_ones()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("runs.pptx"),
        [new TestSlideSpec { Title = "T", WithEmptyRuns = true }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal(3, slide.Shapes.Count);
    Assert.Equal("AlphaBetaGamma", Assert.IsType<PptxTextBoxShape>(slide.Shapes[2]).Text);
  }

  [Fact]
  public async Task Reads_a_table_with_its_dimensions_and_every_cell_addressed()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("table.pptx"),
        [new TestSlideSpec { Title = "T", WithTable = true, TableCells = [["a", "b", "c"], ["d", "e", "f"]] }]);
    PptxTableShape table = Assert.IsType<PptxTableShape>((await ReadSlide(path, 1)).Shapes[2]);
    Assert.Equal(2, table.Rows);
    Assert.Equal(3, table.Cols);
    Assert.Equal(["a", "b", "c", "d", "e", "f"], table.Cells.Select(cell => cell.Text));
    Assert.Equal([(1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3)],
        table.Cells.Select(cell => (cell.Row, cell.Col)));
  }

  [Fact]
  public async Task Addresses_every_table_cell_through_its_table_shape()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("cells.pptx"),
        [new TestSlideSpec { Title = "T", WithTable = true, TableCells = [["a", "b", "c"], ["d", "e", "f"]] }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal("f", CellText(slide, "s1.sh3.t2.c3"));
    Assert.Equal("b", CellText(slide, "s1.sh3.t1.c2"));
  }

  [Fact]
  public async Task Reads_a_chart_with_its_type_series_and_categories()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("chart.pptx"),
        [new TestSlideSpec { Title = "T", WithChart = true }]);
    PptxChartShape chart = Assert.IsType<PptxChartShape>((await ReadSlide(path, 1)).Shapes[2]);
    Assert.Equal("column", chart.ChartType);
    Assert.Equal(["Series 1"], chart.SeriesNames);
    Assert.Equal(["Cat A", "Cat B"], chart.CategoryNames);
  }

  [Fact]
  public async Task Reads_a_group_as_one_shape_whose_children_address_through_it()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("group.pptx"),
        [new TestSlideSpec { Title = "T", WithGroup = true }]);
    PptxGroupShape group = Assert.IsType<PptxGroupShape>((await ReadSlide(path, 1)).Shapes[2]);
    Assert.Equal("s1.sh3", group.Address);
    Assert.Equal(["s1.sh3.sh1", "s1.sh3.sh2"], group.Children.Select(child => child.Address));
    Assert.Equal(["Grouped one", "Grouped two"],
        group.Children.Select(child => Assert.IsType<PptxTextBoxShape>(child).Text));
  }

  [Fact]
  public async Task Reads_speaker_notes_as_plain_text()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("notes.pptx"),
        [new TestSlideSpec { Title = "T", Notes = "Talk about batteries\nand chargers" }]);
    Assert.Equal("Talk about batteries\nand chargers", (await ReadSlide(path, 1)).Notes);
  }

  [Fact]
  public async Task Reports_empty_notes_as_the_empty_string_never_null()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("nonotes.pptx"), [new TestSlideSpec { Title = "T" }]);
    Assert.Equal(string.Empty, (await ReadSlide(path, 1)).Notes);
  }

  [Fact]
  public async Task Reads_a_hidden_slide_normally_and_flags_it()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("hidden.pptx"),
        [new TestSlideSpec { Title = "Shown" }, new TestSlideSpec { Title = "Hidden", Body = "still here", Hidden = true }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 2);
    Assert.True(slide.Hidden);
    Assert.Equal("Hidden", Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]).Text);
    Assert.Equal("still here", Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]).Text);
  }

  [Fact]
  public async Task Carries_the_layout_name_the_slide_number_and_the_files_version()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("layout.pptx"),
        [new TestSlideSpec { Title = "A" }, new TestSlideSpec { Title = "B", LayoutName = "Title Only" }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 2);
    FileInfo info = new(path);
    Assert.Equal("Title Only", slide.LayoutName);
    Assert.Equal(2, slide.Number);
    Assert.Equal(info.Length, slide.Version.Length);
  }

  [Fact]
  public async Task Reads_a_legacy_comment_with_its_author_and_anchor()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("comments.pptx"),
        [new TestSlideSpec { Title = "T", WithLegacyComment = true }]);
    PptxCommentInfo comment = Assert.Single((await ReadSlide(path, 1)).Comments);
    Assert.Equal("Reviewer", comment.Author);
    Assert.Equal("Check this figure", comment.Text);
    Assert.True(comment.Legacy);
    Assert.Equal("s1", comment.AnchorAddress);
  }

  [Fact]
  public async Task Reads_an_animation_effect_in_playback_order_with_its_target()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("anim.pptx"),
        [new TestSlideSpec { Title = "T", AnimationEffects = 1 }]);
    PptxAnimationInfo effect = Assert.Single((await ReadSlide(path, 1)).Animations);
    Assert.Equal(1, effect.Index);
    Assert.Equal("s1.sh1", effect.Address);
    Assert.Equal("entr:10", effect.Effect);
    Assert.Equal("onClick", effect.Trigger);
  }

  [Fact]
  public async Task Reports_no_animations_for_a_timing_element_carrying_no_effect()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("empty-timing.pptx"),
        [new TestSlideSpec { Title = "T", WithEmptyTiming = true }]);
    Assert.Empty((await ReadSlide(path, 1)).Animations);
  }

  [Fact]
  public async Task Reports_no_animations_for_a_slide_with_no_timing_element()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("no-timing.pptx"), [new TestSlideSpec { Title = "T" }]);
    Assert.Empty((await ReadSlide(path, 1)).Animations);
  }

  [Fact]
  public async Task Carries_the_decks_media()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("media.pptx"),
        [new TestSlideSpec { Title = "T", WithPicture = true }]);
    PptxMediaInfo media = Assert.Single((await ReadSlide(path, 1)).Media);
    Assert.Equal("image/png", media.MediaType);
  }

  [Fact]
  public async Task Refuses_slide_zero()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("zero.pptx"));
    Assert.Equal(PptxErrorCodes.SlideNotFound, await SlideCode(path, 0));
  }

  [Fact]
  public async Task Refuses_a_slide_number_past_the_last_slide()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("past.pptx"),
        [new TestSlideSpec { Title = "A" }, new TestSlideSpec { Title = "B" }]);
    Assert.Equal(PptxErrorCodes.SlideNotFound, await SlideCode(path, 3));
  }

  [Fact]
  public async Task Refuses_a_missing_file()
  {
    string code = await SlideCode(ScratchFile("absent.pptx"), 1).ConfigureAwait(true);
    Assert.Equal(PptxErrorCodes.FileNotFound, code);
  }

  [Fact]
  public async Task Refuses_a_package_that_is_not_a_presentation()
  {
    string path = ScratchFile("not-a-deck.pptx");
    await File.WriteAllTextAsync(path, "not a zip", TestContext.Current.CancellationToken);
    Assert.Equal(PptxErrorCodes.NotAPptx, await SlideCode(path, 1));
  }

  [Fact]
  public async Task Resolves_the_shape_addresses_the_slide_reports()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("resolve.pptx"),
        [new TestSlideSpec { Title = "T", Body = "B", WithGroup = true }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal("B", Assert.IsType<PptxTextBoxShape>(Resolved(slide, "s1.sh2")).Text);
    Assert.Equal("Grouped two", Assert.IsType<PptxTextBoxShape>(Resolved(slide, "s1.sh3.sh2")).Text);
  }

  [Fact]
  public async Task Refuses_a_shape_address_the_slide_does_not_have()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("missing-shape.pptx"), [new TestSlideSpec { Title = "T" }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal(PptxErrorCodes.AddressNotFound, ResolveCode(slide, "s1.sh9"));
    Assert.Equal(PptxErrorCodes.AddressNotFound, ResolveCode(slide, "s1.sh1.sh1"));
  }

  [Fact]
  public async Task Refuses_a_table_cell_outside_the_tables_dimensions()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("missing-cell.pptx"),
        [new TestSlideSpec { Title = "T", WithTable = true }]);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal(PptxErrorCodes.AddressNotFound, ResolveCode(slide, "s1.sh3.t3.c1"));
    Assert.Equal(PptxErrorCodes.AddressNotFound, ResolveCode(slide, "s1.sh3.t1.c9"));
  }

  [Fact]
  public async Task Refuses_a_slide_address_where_a_shape_address_is_required()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("slide-address.pptx"));
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal(PptxErrorCodes.AddressNotFound, ResolveCode(slide, "s1"));
    Assert.Equal(PptxErrorCodes.AddressNotFound, ResolveCode(slide, "s1.notes"));
  }

  private static PptxShapeInfo Resolved(PptxSlideSnapshot slide, string address)
  {
    Result<PptxShapeInfo> result = PptxShapeWalker.Resolve(slide.Shapes, Parsed(address));
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private static string ResolveCode(PptxSlideSnapshot slide, string address)
  {
    Result<PptxShapeInfo> result = PptxShapeWalker.Resolve(slide.Shapes, Parsed(address));
    Assert.False(result.IsSuccess);
    return result.Error.Code;
  }

  private static PptxAddress Parsed(string address)
  {
    Result<PptxAddress> parsed = PptxAddress.Parse(address);
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    return parsed.Value;
  }

  private static string CellText(PptxSlideSnapshot slide, string address)
  {
    PptxAddress parsed = Parsed(address);
    PptxTableShape table = Assert.IsType<PptxTableShape>(Resolved(slide, $"s{parsed.Slide}.sh{string.Join(".sh", parsed.ShapePath)}"));
    return table.Cells.Single(cell => cell.Row == parsed.Row && cell.Col == parsed.Col).Text;
  }

  private async Task<PptxSlideSnapshot> ReadSlide(string path, int slide)
  {
    Result<PptxSlideSnapshot> result = await _access.ReadSlideAsync(path, slide).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<string> SlideCode(string path, int slide)
  {
    Result<PptxSlideSnapshot> result = await _access.ReadSlideAsync(path, slide).ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    return result.Error.Code;
  }
}

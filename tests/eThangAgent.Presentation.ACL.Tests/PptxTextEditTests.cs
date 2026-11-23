using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The atomic text edit: replaceText and setNotes applied as one version-guarded
///     batch, the formatting and geometry an edit leaves alone, the notes part a slide
///     gains, and the refusals that leave the file byte-identical.</summary>
public sealed class PptxTextEditTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-edit").FullName, name);

  [Fact]
  public async Task Replaces_a_slides_title_and_reports_a_new_version()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("title.pptx"),
    [
      new TestSlideSpec { Title = "Chapter 1: Mobile Devices I", Body = "Covers batteries" },
      new TestSlideSpec { Title = "Chapter 1: Mobile Devices II", Body = "Covers displays", Notes = "Speaker notes", WithPicture = true },
    ]);
    FileVersion before = (await Outline(path)).Version;
    PptxEditOutcome outcome = await Edit(path, before, new ReplaceText("s2.sh1", "Chapter 1: Mobile Devices III"));
    Assert.Equal(1, outcome.ChangedCount);
    Assert.NotEqual(before, outcome.NewVersion);
    PptxSlideSnapshot slide = await SlideRead(path, 2);
    Assert.Equal("Chapter 1: Mobile Devices III", Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]).Text);
    Assert.Equal("Covers displays", Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]).Text);
    Assert.Equal("picture", slide.Shapes[2].Kind);
    Assert.Equal("Speaker notes", slide.Notes);
  }

  [Fact]
  public async Task Reports_the_version_a_subsequent_read_reports()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("version.pptx"));
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version, new ReplaceText("s1.sh1", "Renamed"));
    Assert.Equal((await Outline(path)).Version, outcome.NewVersion);
  }

  [Fact]
  public async Task Keeps_the_boxes_position_size_and_character_formatting()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("format.pptx"),
        [new TestSlideSpec { Title = "Original", WithFormattedTitle = true }]);
    string before = ShapeXml(path, 1, 1);
    _ = await Edit(path, (await Outline(path)).Version, new ReplaceText("s1.sh1", "Replacement"));
    string after = ShapeXml(path, 1, 1);
    Assert.Contains("x=\"838200\"", after, StringComparison.Ordinal);
    Assert.Contains("cx=\"10515600\"", after, StringComparison.Ordinal);
    Assert.Contains("sz=\"2400\"", after, StringComparison.Ordinal);
    Assert.Contains("b=\"1\"", after, StringComparison.Ordinal);
    Assert.Contains("algn=\"ctr\"", after, StringComparison.Ordinal);
    Assert.Contains("<a:t>Replacement</a:t>", after, StringComparison.Ordinal);
    Assert.DoesNotContain("Original", after, StringComparison.Ordinal);
    Assert.Equal(ShapePropertiesOf(before), ShapePropertiesOf(after));
  }

  [Fact]
  public async Task Empties_a_box_rather_than_deleting_it()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("empty.pptx"),
        [new TestSlideSpec { Title = "Title", Body = "Body text" }]);
    _ = await Edit(path, (await Outline(path)).Version, new ReplaceText("s1.sh2", string.Empty));
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    Assert.Equal(2, slide.Shapes.Count);
    Assert.Equal(string.Empty, Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]).Text);
  }

  [Fact]
  public async Task Replaces_a_table_cells_text_and_leaves_the_table_intact()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("cell.pptx"),
        [new TestSlideSpec { Title = "T", WithTable = true, TableCells = [["a", "b"], ["c", "d"]] }]);
    _ = await Edit(path, (await Outline(path)).Version, new ReplaceText("s1.sh3.t1.c2", "B"));
    PptxTableShape table = Assert.IsType<PptxTableShape>((await SlideRead(path, 1)).Shapes[2]);
    Assert.Equal(2, table.Rows);
    Assert.Equal(2, table.Cols);
    Assert.Equal(["a", "B", "c", "d"], table.Cells.Select(cell => cell.Text));
  }

  [Fact]
  public async Task Writes_notes_on_a_slide_that_has_none()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("notes-new.pptx"),
        [new TestSlideSpec { Title = "T", Body = "B" }]);
    _ = await Edit(path, (await Outline(path)).Version, new SetNotes(1, "Notes written by the edit"));
    Assert.Equal("Notes written by the edit", (await SlideRead(path, 1)).Notes);
  }

  [Fact]
  public async Task Replaces_one_slides_notes_and_leaves_the_others_alone()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("notes-two.pptx"),
    [
      new TestSlideSpec { Title = "One", Notes = "First notes" },
      new TestSlideSpec { Title = "Two", Notes = "Second notes" },
    ]);
    _ = await Edit(path, (await Outline(path)).Version, new SetNotes(2, "Replaced notes"));
    Assert.Equal("First notes", (await SlideRead(path, 1)).Notes);
    Assert.Equal("Replaced notes", (await SlideRead(path, 2)).Notes);
  }

  [Fact]
  public async Task Writes_multi_line_notes_as_separate_paragraphs()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("notes-lines.pptx"));
    _ = await Edit(path, (await Outline(path)).Version, new SetNotes(1, "first\nsecond"));
    Assert.Equal("first\nsecond", (await SlideRead(path, 1)).Notes);
  }

  [Fact]
  public async Task Refuses_the_whole_batch_when_a_later_operation_addresses_nothing()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("atomic.pptx"),
        [new TestSlideSpec { Title = "Original title", Body = "Body" }]);
    byte[] before = Bytes(path);
    string code = await EditCode(path, (await Outline(path)).Version,
        new ReplaceText("s1.sh1", "Replacement title"),
        new ReplaceText("s1.sh9", "Nowhere"));
    Assert.Equal(PptxErrorCodes.AddressNotFound, code);
    Assert.Equal(before, Bytes(path));
    Assert.Equal("Original title", Assert.IsType<PptxTextBoxShape>((await SlideRead(path, 1)).Shapes[0]).Text);
  }

  [Fact]
  public async Task Refuses_a_batch_whose_expected_version_is_stale()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("stale.pptx"));
    FileVersion first = (await Outline(path)).Version;
    _ = await Edit(path, first, new ReplaceText("s1.sh1", "First edit"));
    byte[] current = Bytes(path);
    string code = await EditCode(path, first, new ReplaceText("s1.sh1", "Second edit"));
    Assert.Equal(PptxErrorCodes.StalePresentation, code);
    Assert.Equal(current, Bytes(path));
    Assert.Equal("First edit", Assert.IsType<PptxTextBoxShape>((await SlideRead(path, 1)).Shapes[0]).Text);
  }

  [Fact]
  public async Task Refuses_an_operation_type_this_build_does_not_apply()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("unsupported.pptx"),
        [new TestSlideSpec { Title = "One" }, new TestSlideSpec { Title = "Two" }]);
    byte[] before = Bytes(path);
    string code = await EditCode(path, (await Outline(path)).Version, new DeleteSlide(1));
    Assert.Equal(PptxErrorCodes.OperationUnsupported, code);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_address_that_does_not_parse()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("bad-address.pptx"));
    byte[] before = Bytes(path);
    string code = await EditCode(path, (await Outline(path)).Version, new ReplaceText("slide1", "Text"));
    Assert.Equal(PptxErrorCodes.InvalidAddress, code);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Refuses_notes_for_a_slide_the_deck_does_not_have()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("bad-slide.pptx"));
    byte[] before = Bytes(path);
    string code = await EditCode(path, (await Outline(path)).Version, new SetNotes(9, "Notes"));
    Assert.Equal(PptxErrorCodes.SlideNotFound, code);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_empty_operation_list()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("no-operations.pptx"));
    string code = await EditCode(path, (await Outline(path)).Version);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, code);
  }

  [Fact]
  public async Task Counts_one_changed_shape_when_two_operations_address_it()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("dedupe.pptx"),
        [new TestSlideSpec { Title = "Original", Body = "Body" }]);
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version,
        new ReplaceText("s1.sh1", "First"),
        new ReplaceText("s1.sh1", "Second"));
    Assert.Equal(1, outcome.ChangedCount);
    Assert.Equal("Second", Assert.IsType<PptxTextBoxShape>((await SlideRead(path, 1)).Shapes[0]).Text);
  }

  [Fact]
  public async Task Leaves_every_unaddressed_shape_the_theme_and_the_media_unchanged()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("fidelity.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body one", Notes = "Notes one", WithPicture = true, WithTable = true },
      new TestSlideSpec { Title = "Two", Body = "Body two", Hidden = true, WithPicture = true },
    ]);
    PptxOutline before = await Outline(path);
    byte[] masterBefore = MasterBytes(path);
    string slideOneBefore = ShapeXml(path, 1, 2);
    _ = await Edit(path, before.Version, new ReplaceText("s1.sh1", "One renamed"));
    PptxOutline after = await Outline(path);
    Assert.Equal(before.SlideCount, after.SlideCount);
    Assert.Equal(before.Media.Select(media => media.Name), after.Media.Select(media => media.Name));
    Assert.Equal(before.LayoutNames, after.LayoutNames);
    Assert.Equal(masterBefore, MasterBytes(path));
    Assert.Equal(slideOneBefore, ShapeXml(path, 1, 2));
    PptxSlideSnapshot second = await SlideRead(path, 2);
    Assert.Equal("Two", Assert.IsType<PptxTextBoxShape>(second.Shapes[0]).Text);
    Assert.True(second.Hidden);
  }

  private static byte[] Bytes(string path) => File.ReadAllBytes(path);

  /// <summary>The slide master's bytes: the deck's design carrier in a fixture deck, which
  ///     carries no separate theme part.</summary>
  private static byte[] MasterBytes(string path)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlideMasterPart master = doc.PresentationPart!.SlideMasterParts.First();
    using MemoryStream buffer = new();
    using (Stream stream = master.GetStream())
    {
      stream.CopyTo(buffer);
    }

    return buffer.ToArray();
  }

  private static string ShapeXml(string path, int slide, int index)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlidePart part = doc.PresentationPart!.SlideParts.ElementAt(slide - 1);
    OpenXmlElement shape = part.Slide!.CommonSlideData!.ShapeTree!.ChildElements
        .Where(child => child is P.Shape or P.Picture or P.GraphicFrame or P.GroupShape or P.ConnectionShape)
        .ElementAt(index - 1);
    return shape.OuterXml;
  }

  private static string ShapePropertiesOf(string shapeXml)
  {
    int start = shapeXml.IndexOf("<p:spPr", StringComparison.Ordinal);
    int end = shapeXml.IndexOf("</p:spPr>", StringComparison.Ordinal);
    return start < 0 || end < 0 ? string.Empty : shapeXml[start..end];
  }

  private async Task<PptxEditOutcome> Edit(string path, FileVersion version, params PptxOperation[] operations)
  {
    Result<PptxEditOutcome> result = await _access.EditAsync(path, version, operations).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<string> EditCode(string path, FileVersion version, params PptxOperation[] operations)
  {
    Result<PptxEditOutcome> result = await _access.EditAsync(path, version, operations).ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    return result.Error.Code;
  }

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxSlideSnapshot> SlideRead(string path, int slide)
  {
    Result<PptxSlideSnapshot> result = await _access.ReadSlideAsync(path, slide).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }
}

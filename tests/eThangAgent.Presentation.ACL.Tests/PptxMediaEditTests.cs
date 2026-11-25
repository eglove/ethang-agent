using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The media and content insertions: a picture's bytes swapped behind its shape,
///     a picture, a table and a text box added to a slide, and the refusals that leave the
///     file byte-identical.</summary>
public sealed class PptxMediaEditTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-media").FullName, name);

  [Fact]
  public async Task Replaces_a_pictures_bytes_so_they_extract_byte_identical()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("replace.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    string source = TestPptxImage.WritePng(ScratchFile("source.png"));
    PptxSlideSnapshot before = await SlideRead(path, 1);
    PptxPictureShape picture = Assert.IsType<PptxPictureShape>(before.Shapes[2]);
    _ = await Edit(path, before.Version, new ReplacePicture(picture.Address, source));
    IReadOnlyList<ToolResultImage> images = await Extract(path, [picture.MediaName]);
    ToolResultImage image = Assert.Single(images);
    Assert.Equal(TestPptxImage.PngBytes(), Convert.FromBase64String(image.Base64Data));
  }

  [Fact]
  public async Task Keeps_the_pictures_position_size_and_effects()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("geometry.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    string source = TestPptxImage.WritePng(ScratchFile("geometry.png"));
    _ = await Edit(path, (await Outline(path)).Version, new ReplacePicture("s1.sh3", source));
    string after = ShapeXml(path, 1, 3);
    Assert.Contains("x=\"914400\"", after, StringComparison.Ordinal);
    Assert.Contains("cx=\"914400\"", after, StringComparison.Ordinal);
    Assert.Contains("blipFill", after, StringComparison.Ordinal);
    Assert.Contains("<a:stretch", after, StringComparison.Ordinal);
    Assert.Contains("<a:picLocks noChangeAspect=\"1\"", after, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Follows_the_relationship_when_the_format_changes()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("format-change.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    string source = TestPptxImage.WriteJpeg(ScratchFile("format.jpg"));
    _ = await Edit(path, (await Outline(path)).Version, new ReplacePicture("s1.sh3", source));
    PptxSlideSnapshot after = await SlideRead(path, 1);
    PptxPictureShape picture = Assert.IsType<PptxPictureShape>(after.Shapes[2]);
    ToolResultImage image = Assert.Single(await Extract(path, [picture.MediaName]));
    Assert.Equal("image/jpeg", image.MediaType);
    Assert.Equal(TestPptxImage.JpegBytes(), Convert.FromBase64String(image.Base64Data));
  }

  [Fact]
  public async Task Leaves_a_media_part_another_slide_shares_unchanged()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("shared.pptx"),
    [
      new TestSlideSpec { Title = "One", WithPicture = true },
      new TestSlideSpec { Title = "Two", WithPicture = true },
    ]);
    ShareMediaPart(path);
    PptxSlideSnapshot before = await SlideRead(path, 1);
    string sharedName = Assert.IsType<PptxPictureShape>(before.Shapes[2]).MediaName;
    string source = TestPptxImage.WritePng(ScratchFile("shared.png"));
    _ = await Edit(path, before.Version, new ReplacePicture("s1.sh3", source));
    PptxSlideSnapshot second = await SlideRead(path, 2);
    PptxPictureShape untouched = Assert.IsType<PptxPictureShape>(second.Shapes[2]);
    Assert.Equal(sharedName, untouched.MediaName);
    ToolResultImage image = Assert.Single(await Extract(path, [sharedName]));
    Assert.Equal(Convert.FromBase64String(TestPptxFactory.PngBase64), Convert.FromBase64String(image.Base64Data));
  }

  [Fact]
  public async Task Refuses_a_replacement_that_is_not_png_or_jpeg()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("gif.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    byte[] before = await Bytes(path);
    string source = TestPptxImage.WriteGif(ScratchFile("source.gif"));
    string code = await EditCode(path, (await Outline(path)).Version, new ReplacePicture("s1.sh3", source));
    Assert.Equal(PptxErrorCodes.ImageNotAttachable, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_picture_path_that_does_not_exist()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("missing-image.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, (await Outline(path)).Version,
        new ReplacePicture("s1.sh3", ScratchFile("absent.png")));
    Assert.Equal(PptxErrorCodes.FileNotFound, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_address_that_is_not_a_picture()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("not-a-picture.pptx"),
        [new TestSlideSpec { Title = "One", Body = "Body" }]);
    string source = TestPptxImage.WritePng(ScratchFile("not-a-picture.png"));
    string code = await EditCode(path, (await Outline(path)).Version, new ReplacePicture("s1.sh1", source));
    Assert.Equal(PptxErrorCodes.AddressNotFound, code);
  }

  [Fact]
  public async Task Inserts_a_picture_at_the_stated_offset_and_size()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("insert-picture.pptx"));
    string source = TestPptxImage.WritePng(ScratchFile("insert.png"));
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version,
        new InsertPicture(1, source, 914400L, 914400L, 1828800L, 1371600L));
    Assert.Equal(1, outcome.ChangedCount);
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    PptxPictureShape picture = Assert.IsType<PptxPictureShape>(slide.Shapes[2]);
    Assert.Equal("s1.sh3", picture.Address);
    string xml = ShapeXml(path, 1, 3);
    Assert.Contains("x=\"914400\"", xml, StringComparison.Ordinal);
    Assert.Contains("y=\"914400\"", xml, StringComparison.Ordinal);
    Assert.Contains("cx=\"1828800\"", xml, StringComparison.Ordinal);
    Assert.Contains("cy=\"1371600\"", xml, StringComparison.Ordinal);
    ToolResultImage image = Assert.Single(await Extract(path, [picture.MediaName]));
    Assert.Equal(TestPptxImage.PngBytes(), Convert.FromBase64String(image.Base64Data));
  }

  [Fact]
  public async Task Registers_the_media_part_and_its_relationship_together()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("relationship.pptx"));
    string source = TestPptxImage.WriteJpeg(ScratchFile("relationship.jpg"));
    _ = await Edit(path, (await Outline(path)).Version, new InsertPicture(1, source, 0L, 0L, 914400L, 914400L));
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlidePart slide = doc.PresentationPart!.SlideParts.First();
    ImagePart part = Assert.Single(slide.ImageParts);
    Assert.Equal(slide.GetIdOfPart(part),
        slide.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().Single().Embed!.Value);
  }

  [Fact]
  public async Task Inserts_a_table_that_reads_back_with_its_dimensions_and_cells()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("insert-table.pptx"));
    IReadOnlyList<IReadOnlyList<string>> cells = [["a", "b", "c"], ["d", "e", "f"]];
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version,
        new InsertTable(1, 2, 3, cells, 914400L, 1828800L, 3657600L, 2743200L));
    Assert.Equal(1, outcome.ChangedCount);
    PptxTableShape table = Assert.IsType<PptxTableShape>((await SlideRead(path, 1)).Shapes[2]);
    Assert.Equal(2, table.Rows);
    Assert.Equal(3, table.Cols);
    Assert.Equal(["a", "b", "c", "d", "e", "f"], table.Cells.Select(cell => cell.Text));
    Assert.Equal(2, table.Cells[5].Row);
    Assert.Equal(3, table.Cells[5].Col);
  }

  [Fact]
  public async Task Addresses_every_inserted_table_cell()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("table-cells.pptx"));
    IReadOnlyList<IReadOnlyList<string>> cells = [["a", "b"], ["c", "d"]];
    _ = await Edit(path, (await Outline(path)).Version,
        new InsertTable(1, 2, 2, cells, 0L, 0L, 914400L, 914400L));
    _ = await Edit(path, (await Outline(path)).Version, new ReplaceText("s1.sh3.t2.c2", "replaced"));
    PptxTableShape table = Assert.IsType<PptxTableShape>((await SlideRead(path, 1)).Shapes[2]);
    Assert.Equal("replaced", table.Cells[3].Text);
  }

  [Fact]
  public async Task Refuses_a_cell_grid_that_does_not_match_the_dimensions()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("ragged-table.pptx"));
    byte[] before = await Bytes(path);
    IReadOnlyList<IReadOnlyList<string>> cells = [["a", "b", "c"], ["d", "e"]];
    string code = await EditCode(path, (await Outline(path)).Version,
        new InsertTable(1, 2, 3, cells, 0L, 0L, 914400L, 914400L));
    Assert.Equal(PptxErrorCodes.InvalidAddress, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_table_whose_cell_count_is_not_rows_times_cols()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("short-table.pptx"));
    IReadOnlyList<IReadOnlyList<string>> cells = [["a", "b", "c"], ["d", "e"]];
    string code = await EditCode(path, (await Outline(path)).Version,
        new InsertTable(1, 2, 3, cells, 0L, 0L, 914400L, 914400L));
    Assert.Equal(PptxErrorCodes.InvalidAddress, code);
  }

  [Fact]
  public async Task Inserts_a_text_box_with_its_text_and_geometry()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("insert-text.pptx"));
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version,
        new InsertTextBox(1, "First line\nSecond line", 914400L, 914400L, 1828800L, 914400L));
    Assert.Equal(1, outcome.ChangedCount);
    PptxTextBoxShape box = Assert.IsType<PptxTextBoxShape>((await SlideRead(path, 1)).Shapes[2]);
    Assert.Equal("First line\nSecond line", box.Text);
    Assert.False(box.IsTitle);
    Assert.False(box.IsPlaceholder);
    string xml = ShapeXml(path, 1, 3);
    Assert.Contains("cx=\"1828800\"", xml, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Inserts_several_shapes_in_one_batch_as_separate_changes()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("batch.pptx"));
    string source = TestPptxImage.WritePng(ScratchFile("batch.png"));
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version,
        new InsertPicture(1, source, 0L, 0L, 914400L, 914400L),
        new InsertTextBox(1, "Caption", 0L, 914400L, 914400L, 914400L));
    Assert.Equal(2, outcome.ChangedCount);
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    Assert.Equal(4, slide.Shapes.Count);
    Assert.Equal("picture", slide.Shapes[2].Kind);
    Assert.Equal("Caption", Assert.IsType<PptxTextBoxShape>(slide.Shapes[3]).Text);
  }

  [Fact]
  public async Task Refuses_a_shape_with_no_extent()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("no-extent.pptx"));
    byte[] before = await Bytes(path);
    string code = await EditCode(path, (await Outline(path)).Version,
        new InsertTextBox(1, "Text", 0L, 0L, 0L, 914400L));
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_insertion_on_a_slide_the_deck_does_not_have()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("no-slide.pptx"));
    string code = await EditCode(path, (await Outline(path)).Version,
        new InsertTextBox(9, "Text", 0L, 0L, 914400L, 914400L));
    Assert.Equal(PptxErrorCodes.SlideNotFound, code);
  }

  [Fact]
  public async Task Leaves_every_other_slide_the_theme_and_the_notes_unchanged()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("fidelity.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body one", Notes = "Notes one", WithPicture = true },
      new TestSlideSpec { Title = "Two", Body = "Body two", Hidden = true },
    ]);
    PptxOutline before = await Outline(path);
    byte[] masterBefore = MasterBytes(path);
    string secondBefore = ShapeXml(path, 2, 1);
    string source = TestPptxImage.WritePng(ScratchFile("fidelity.png"));
    _ = await Edit(path, before.Version, new InsertPicture(1, source, 0L, 0L, 914400L, 914400L));
    PptxOutline after = await Outline(path);
    Assert.Equal(before.SlideCount, after.SlideCount);
    Assert.Equal(before.LayoutNames, after.LayoutNames);
    Assert.Equal(masterBefore, MasterBytes(path));
    Assert.Equal(secondBefore, ShapeXml(path, 2, 1));
    Assert.Equal("Notes one", (await SlideRead(path, 1)).Notes);
  }

  /// <summary>Makes the second slide display the first slide's media part, which is what a
  ///     deck does when one picture is copied onto another slide.</summary>
  private static void ShareMediaPart(string path)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, true);
    SlidePart first = doc.PresentationPart!.SlideParts.ElementAt(0);
    SlidePart second = doc.PresentationPart.SlideParts.ElementAt(1);
    ImagePart shared = first.ImageParts.First();
    _ = second.AddPart(shared);
    P.Picture picture = second.Slide!.Descendants<P.Picture>().First();
    picture.BlipFill!.Blip!.Embed = second.GetIdOfPart(shared);
    doc.Save();
  }

  private static async Task<byte[]> Bytes(string path)
    => await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);

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

  private async Task<IReadOnlyList<ToolResultImage>> Extract(string path, IReadOnlyList<string>? names)
  {
    Result<IReadOnlyList<ToolResultImage>> result = await _access.ExtractImagesAsync(path, names).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }
}

using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The structural slide edits: inserting, deleting and moving slides, the order
///     and text every one leaves behind, the parts a deletion removes, and the refusals
///     that leave the file byte-identical.</summary>
public sealed class PptxSlideStructureTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-structure").FullName, name);

  [Fact]
  public async Task Inserts_a_slide_after_the_named_position()
  {
    string path = ThreeSlides("insert-middle.pptx");
    _ = await Edit(path, new InsertSlide(1, "Title and Content"));
    PptxOutline outline = await Outline(path);
    Assert.Equal(4, outline.SlideCount);
    Assert.Equal(["One", string.Empty, "Two", "Three"], outline.Slides.Select(slide => slide.Title));
  }

  [Fact]
  public async Task Inserts_at_the_front_when_after_slide_is_zero()
  {
    string path = ThreeSlides("insert-front.pptx");
    _ = await Edit(path, new InsertSlide(0, "Title and Content"));
    PptxOutline outline = await Outline(path);
    Assert.Equal(4, outline.SlideCount);
    Assert.Equal(string.Empty, outline.Slides[0].Title);
    Assert.Equal("One", outline.Slides[1].Title);
  }

  [Fact]
  public async Task Inserts_at_the_end_when_after_slide_is_the_last_slide()
  {
    string path = ThreeSlides("insert-end.pptx");
    _ = await Edit(path, new InsertSlide(3, "Title and Content"));
    PptxOutline outline = await Outline(path);
    Assert.Equal(string.Empty, outline.Slides[3].Title);
    Assert.Equal("Three", outline.Slides[2].Title);
  }

  [Fact]
  public async Task Builds_the_new_slide_on_the_named_layout_with_its_placeholders()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("insert-layout.pptx"),
    [
      new TestSlideSpec { Title = "One", LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Only" },
    ]);
    _ = await Edit(path, new InsertSlide(1, "Title Only"));
    PptxOutline outline = await Outline(path);
    Assert.Equal("Title Only", outline.Slides[1].LayoutName);
    PptxSlideSnapshot slide = await SlideRead(path, 2);
    PptxTextBoxShape title = Assert.IsType<PptxTextBoxShape>(Assert.Single(slide.Shapes));
    Assert.True(title.IsTitle);
    Assert.True(title.IsPlaceholder);
    Assert.Equal(string.Empty, title.Text);
  }

  [Fact]
  public async Task Gives_the_inserted_slide_the_layouts_geometry()
  {
    string path = ThreeSlides("insert-geometry.pptx");
    _ = await Edit(path, new InsertSlide(0, "Title and Content"));
    string xml = ShapeXml(path, 1, 1);
    Assert.Contains("x=\"838200\"", xml, StringComparison.Ordinal);
    Assert.Contains("cy=\"1325563\"", xml, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Leaves_every_other_slides_text_unchanged_after_an_insert()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("insert-text.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "First body", Notes = "First notes" },
      new TestSlideSpec { Title = "Two", Body = "Second body", Notes = "Second notes" },
    ]);
    _ = await Edit(path, new InsertSlide(1, "Title and Content"));
    PptxSlideSnapshot second = await SlideRead(path, 3);
    Assert.Equal("Two", Assert.IsType<PptxTextBoxShape>(second.Shapes[0]).Text);
    Assert.Equal("Second body", Assert.IsType<PptxTextBoxShape>(second.Shapes[1]).Text);
    Assert.Equal("Second notes", second.Notes);
  }

  [Fact]
  public async Task Refuses_an_unknown_layout_and_lists_the_available_names()
  {
    string path = ThreeSlides("insert-unknown-layout.pptx");
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new InsertSlide(1, "No Such Layout"));
    Assert.Contains("LayoutNotFound", message, StringComparison.Ordinal);
    Assert.Contains("Title and Content", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_after_slide_outside_the_deck()
  {
    string path = ThreeSlides("insert-bad-position.pptx");
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new InsertSlide(4, "Title and Content"));
    Assert.Contains("SlideNotFound", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Deletes_a_slide_and_renumbers_the_rest()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("delete.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "First body" },
      new TestSlideSpec { Title = "Two", Body = "Second body" },
      new TestSlideSpec { Title = "Three", Body = "Third body" },
    ]);
    _ = await Edit(path, new DeleteSlide(1));
    PptxOutline outline = await Outline(path);
    Assert.Equal(2, outline.SlideCount);
    Assert.Equal(["Two", "Three"], outline.Slides.Select(slide => slide.Title));
    Assert.Equal([1, 2], outline.Slides.Select(slide => slide.Number));
    PptxSlideSnapshot first = await SlideRead(path, 1);
    Assert.Equal("Two", Assert.IsType<PptxTextBoxShape>(first.Shapes[0]).Text);
    Assert.Equal("Second body", Assert.IsType<PptxTextBoxShape>(first.Shapes[1]).Text);
  }

  [Fact]
  public async Task Removes_the_deleted_slides_part_and_its_notes_so_nothing_is_orphaned()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("delete-parts.pptx"),
    [
      new TestSlideSpec { Title = "One", Notes = "First notes", WithPicture = true },
      new TestSlideSpec { Title = "Two", Notes = "Second notes", WithPicture = true },
    ]);
    _ = await Edit(path, new DeleteSlide(1));
    Assert.DoesNotContain(OrphanedEntries(path), entry => entry.Contains("notesSlide", StringComparison.Ordinal));
    Assert.Empty(OrphanedEntries(path));
  }

  [Fact]
  public async Task Keeps_the_slide_ids_unique_and_increasing()
  {
    string path = ThreeSlides("delete-ids.pptx");
    _ = await Edit(path, new DeleteSlide(2));
    List<uint> ids = SlideIds(path);
    Assert.Equal(2, ids.Count);
    Assert.Equal(ids.Count, ids.Distinct().Count());
    Assert.True(ids.SequenceEqual(ids.Order()));
    Assert.All(ids, id => Assert.True(id >= 256U));
  }

  [Fact]
  public async Task Refuses_to_delete_the_last_remaining_slide()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("delete-last.pptx"));
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new DeleteSlide(1));
    Assert.Contains("StrandedContent", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
    Assert.Equal(1, (await Outline(path)).SlideCount);
  }

  [Fact]
  public async Task Moves_a_slide_forward_and_renumbers()
  {
    string path = ThreeSlides("move-forward.pptx");
    _ = await Edit(path, new MoveSlide(1, 3));
    PptxOutline outline = await Outline(path);
    Assert.Equal(["Two", "Three", "One"], outline.Slides.Select(slide => slide.Title));
    Assert.Equal([1, 2, 3], outline.Slides.Select(slide => slide.Number));
  }

  [Fact]
  public async Task Moves_a_slide_backward_and_renumbers()
  {
    string path = ThreeSlides("move-backward.pptx");
    _ = await Edit(path, new MoveSlide(3, 1));
    Assert.Equal(["Three", "One", "Two"], (await Outline(path)).Slides.Select(slide => slide.Title));
  }

  [Fact]
  public async Task Moves_a_slide_into_the_middle()
  {
    string path = ThreeSlides("move-middle.pptx");
    _ = await Edit(path, new MoveSlide(1, 2));
    Assert.Equal(["Two", "One", "Three"], (await Outline(path)).Slides.Select(slide => slide.Title));
  }

  [Fact]
  public async Task Accepts_a_move_that_changes_nothing()
  {
    string path = ThreeSlides("move-noop.pptx");
    PptxEditOutcome outcome = await Edit(path, new MoveSlide(2, 2));
    Assert.Equal(0, outcome.ChangedCount);
    PptxOutline outline = await Outline(path);
    Assert.Equal(["One", "Two", "Three"], outline.Slides.Select(slide => slide.Title));
    Assert.Equal([1, 2, 3], outline.Slides.Select(slide => slide.Number));
    Assert.Equal("Second body", Assert.IsType<PptxTextBoxShape>((await SlideRead(path, 2)).Shapes[1]).Text);
  }

  [Fact]
  public async Task Refuses_a_move_from_or_to_a_slide_the_deck_does_not_have()
  {
    string path = ThreeSlides("move-bad.pptx");
    byte[] before = Bytes(path);
    Assert.Contains("SlideNotFound", await EditMessage(path, new MoveSlide(4, 1)), StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
    Assert.Contains("SlideNotFound", await EditMessage(path, new MoveSlide(1, 9)), StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Leaves_the_theme_master_and_layouts_untouched()
  {
    string path = ThreeSlides("structure-design.pptx");
    Dictionary<string, string> before = DesignParts(path);
    _ = await Edit(path, new InsertSlide(1, "Title and Content"), new MoveSlide(1, 3), new DeleteSlide(2));
    Assert.Equal(before, DesignParts(path));
  }

  [Fact]
  public async Task Applies_a_structural_batch_atomically()
  {
    string path = ThreeSlides("structure-atomic.pptx");
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new DeleteSlide(1), new DeleteSlide(99));
    Assert.Contains("SlideNotFound", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
    Assert.Equal(3, (await Outline(path)).SlideCount);
  }

  private static string ThreeSlides(string name)
    => TestPptxFactory.CreateDeck(ScratchFile(name),
    [
      new TestSlideSpec { Title = "One", Body = "First body" },
      new TestSlideSpec { Title = "Two", Body = "Second body" },
      new TestSlideSpec { Title = "Three", Body = "Third body" },
    ]);

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> outline = await _access.ReadOutlineAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outline.IsSuccess, outline.IsSuccess ? string.Empty : outline.Error.Message);
    return outline.Value;
  }

  private async Task<PptxSlideSnapshot> SlideRead(string path, int slide)
  {
    Result<PptxSlideSnapshot> snapshot = await _access.ReadSlideAsync(path, slide, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(snapshot.IsSuccess, snapshot.IsSuccess ? string.Empty : snapshot.Error.Message);
    return snapshot.Value;
  }

  private async Task<PptxEditOutcome> Edit(string path, params PptxOperation[] operations)
  {
    FileVersion version = await Version(path).ConfigureAwait(true);
    Result<PptxEditOutcome> outcome = await _access.EditAsync(path, version, operations,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outcome.IsSuccess, outcome.IsSuccess ? string.Empty : outcome.Error.Message);
    return outcome.Value;
  }

  private async Task<string> EditMessage(string path, params PptxOperation[] operations)
  {
    FileVersion version = await Version(path).ConfigureAwait(true);
    Result<PptxEditOutcome> outcome = await _access.EditAsync(path, version, operations,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(outcome.IsSuccess);
    return outcome.Error.Code + ": " + outcome.Error.Message;
  }

  private static byte[] Bytes(string path) => File.ReadAllBytes(path);

  /// <summary>The file's current version token, read the way a caller reads it.</summary>
  private async Task<FileVersion> Version(string path)
  {
    Result<PptxOutline> outline = await _access.ReadOutlineAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outline.IsSuccess, outline.IsSuccess ? string.Empty : outline.Error.Message);
    return outline.Value.Version;
  }

  private static string ShapeXml(string path, int slide, int shape)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    PresentationPart part = doc.PresentationPart!;
    List<SlidePart> slides = PptxOutlineReader.SlideParts(part, part.Presentation!);
    return slides[slide - 1].Slide!.CommonSlideData!.ShapeTree!.ChildElements
        .Where(element => element is P.Shape or P.Picture or P.GraphicFrame)
        .ElementAt(shape - 1).OuterXml;
  }

  private static List<uint> SlideIds(string path)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    return [.. doc.PresentationPart!.Presentation!.SlideIdList!.Elements<P.SlideId>()
        .Select(entry => entry.Id?.Value ?? 0U)];
  }

  /// <summary>Every zip entry that no part in the package's graph reaches: a part left
  ///     behind after a deletion, which is what makes PowerPoint offer a repair.</summary>
  private static List<string> OrphanedEntries(string path)
  {
    HashSet<string> reachable = [];
    using (PresentationDocument doc = PresentationDocument.Open(path, false))
    {
      Collect(doc.PresentationPart!, reachable);
    }

    using ZipArchive zip = ZipFile.OpenRead(path);
    return [.. zip.Entries
        .Select(entry => "/" + entry.FullName)
        .Where(entry => !entry.EndsWith(".rels", StringComparison.Ordinal)
            && entry != "/[Content_Types].xml"
            && entry != "/ppt/presentation.xml"
            && !reachable.Contains(entry, StringComparer.OrdinalIgnoreCase))];
  }

  private static void Collect(OpenXmlPartContainer container, HashSet<string> reachable)
  {
    foreach (OpenXmlPart part in container.Parts.Select(pair => pair.OpenXmlPart)
        .Where(part => reachable.Add(part.Uri.OriginalString)))
    {
      Collect(part, reachable);
    }
  }

  /// <summary>Every design part's bytes, keyed by part name: the theme, the master and the
  ///     layouts, which no structural edit may change.</summary>
  private static Dictionary<string, string> DesignParts(string path)
  {
    Dictionary<string, string> parts = [];
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    foreach (SlideMasterPart master in doc.PresentationPart!.SlideMasterParts)
    {
      parts[master.Uri.OriginalString] = master.SlideMaster?.OuterXml ?? string.Empty;
      foreach (SlideLayoutPart layout in master.SlideLayoutParts)
      {
        parts[layout.Uri.OriginalString] = layout.SlideLayout?.OuterXml ?? string.Empty;
      }
    }

    return parts;
  }
}

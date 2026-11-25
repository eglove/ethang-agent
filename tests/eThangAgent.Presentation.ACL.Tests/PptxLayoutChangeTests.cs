using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Changing a slide's layout: which content maps to the target layout's
///     placeholders, what is left in place when it does not map, the refusal that names
///     what would be stranded, and the four placeholder types a real deck uses.</summary>
public sealed class PptxLayoutChangeTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-layout").FullName, name);

  [Fact]
  public async Task Moves_a_title_and_body_onto_the_target_layouts_placeholders()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("map.pptx"),
    [
      new TestSlideSpec { Title = "Title Slide", Body = "Subtitle text", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Other", LayoutName = "Title and Content" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title and Content"));
    Assert.Equal("Title and Content", (await Outline(path)).Slides[0].LayoutName);
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    Assert.Equal("Title Slide", Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]).Text);
    Assert.Equal("Subtitle text", Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]).Text);
  }

  [Fact]
  public async Task Maps_a_centre_title_and_subtitle_onto_a_title_and_body_layout()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("ctrtitle.pptx"),
    [
      new TestSlideSpec { Title = "Chapter 1: Mobile Devices I", Body = "Covers batteries", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Second", LayoutName = "Title and Content" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title and Content"));
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    PptxTextBoxShape title = Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]);
    Assert.True(title.IsTitle);
    Assert.Equal("Chapter 1: Mobile Devices I", title.Text);
    PptxTextBoxShape body = Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]);
    Assert.False(body.IsTitle);
    Assert.Equal("Covers batteries", body.Text);
  }

  [Fact]
  public async Task Maps_a_title_and_body_onto_a_centre_title_and_subtitle_layout()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("subtitle.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body text", LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Slide" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title Slide"));
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    Assert.Equal("One", Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]).Text);
    Assert.Equal("Body text", Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]).Text);
  }

  [Fact]
  public async Task Refuses_a_change_that_would_strand_a_body_box_and_names_it()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("strand.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body text", LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Only" },
    ]);
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new SetSlideLayout(1, "Title Only"));
    Assert.Contains("StrandedContent", message, StringComparison.Ordinal);
    Assert.Contains("s1.sh2", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
    Assert.Equal("Title and Content", (await Outline(path)).Slides[0].LayoutName);
  }

  [Fact]
  public async Task Leaves_a_shape_that_does_not_map_in_place_and_visible()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("in-place.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body text", WithPicture = true, LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Slide" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title Slide"));
    PptxSlideSnapshot slide = await SlideRead(path, 1);
    Assert.Equal(3, slide.Shapes.Count);
    Assert.Equal("picture", slide.Shapes[2].Kind);
    Assert.Equal("One", Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]).Text);
  }

  [Fact]
  public async Task Gives_a_mapped_shape_the_target_placeholders_geometry()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("geometry.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body", LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Slide" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title Slide"));
    string title = ShapeXml(path, 1, 1);
    Assert.Contains("cx=\"10515600\"", title, StringComparison.Ordinal);
    Assert.Contains("cy=\"1325563\"", title, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Reports_the_new_layout_on_a_subsequent_read()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("readback.pptx"),
    [
      new TestSlideSpec { Title = "One", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title and Content" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title and Content"));
    PptxOutline outline = await Outline(path);
    Assert.Equal("Title and Content", outline.Slides[0].LayoutName);
    Assert.Equal("Title and Content", outline.Slides[1].LayoutName);
  }

  [Fact]
  public async Task Leaves_the_other_slides_and_the_deck_design_untouched()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("untouched.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "First body", Notes = "First notes", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Two", Body = "Second body", Notes = "Second notes", LayoutName = "Title and Content" },
    ]);
    Dictionary<string, string> before = DesignParts(path);
    _ = await Edit(path, new SetSlideLayout(1, "Title and Content"));
    Assert.Equal(before, DesignParts(path));
    PptxSlideSnapshot second = await SlideRead(path, 2);
    Assert.Equal("Two", Assert.IsType<PptxTextBoxShape>(second.Shapes[0]).Text);
    Assert.Equal("Second notes", second.Notes);
  }

  [Fact]
  public async Task Keeps_the_notes_of_the_changed_slide()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("notes.pptx"),
    [
      new TestSlideSpec { Title = "One", Notes = "Speaker notes", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title and Content" },
    ]);
    _ = await Edit(path, new SetSlideLayout(1, "Title and Content"));
    Assert.Equal("Speaker notes", (await SlideRead(path, 1)).Notes);
  }

  [Fact]
  public async Task Refuses_an_unknown_layout_and_lists_the_available_names()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("unknown.pptx"),
    [
      new TestSlideSpec { Title = "One", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title and Content" },
    ]);
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new SetSlideLayout(1, "Nowhere"));
    Assert.Contains("LayoutNotFound", message, StringComparison.Ordinal);
    Assert.Contains("Title Slide", message, StringComparison.Ordinal);
    Assert.Contains("Title and Content", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_slide_the_deck_does_not_have()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("bad-slide.pptx"));
    byte[] before = Bytes(path);
    Assert.Contains("SlideNotFound", await EditMessage(path, new SetSlideLayout(9, "Title and Content")), StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
  }

  [Fact]
  public async Task Refuses_the_whole_batch_when_a_layout_change_would_strand_a_shape()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("batch.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body", LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Slide" },
      new TestSlideSpec { Title = "Three", LayoutName = "Title Only" },
    ]);
    byte[] before = Bytes(path);
    string message = await EditMessage(path, new ReplaceText("s2.sh1", "Renamed"), new SetSlideLayout(1, "Title Only"));
    Assert.Contains("StrandedContent", message, StringComparison.Ordinal);
    Assert.Equal(before, Bytes(path));
    Assert.Equal("Two", Assert.IsType<PptxTextBoxShape>((await SlideRead(path, 2)).Shapes[0]).Text);
  }

  [Fact]
  public void Maps_every_placeholder_family_the_teaching_deck_declares()
  {
    Assert.Equal(PptxPlaceholderFamily.Title, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.Title));
    Assert.Equal(PptxPlaceholderFamily.Title, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.CenteredTitle));
    Assert.Equal(PptxPlaceholderFamily.Body, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.Body));
    Assert.Equal(PptxPlaceholderFamily.Body, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.SubTitle));
    Assert.Equal(PptxPlaceholderFamily.Body, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.Object));
    Assert.Equal(PptxPlaceholderFamily.Picture, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.Picture));
    Assert.Equal(PptxPlaceholderFamily.Decoration, PptxLayoutMapper.FamilyOf(P.PlaceholderValues.SlideNumber));
  }

  [Fact]
  public void Matches_two_body_placeholders_by_index_rather_than_by_name()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("two-bodies.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "First", LayoutName = "Title and Content" },
      new TestSlideSpec { Title = "Two", LayoutName = "Title Slide" },
    ]);
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    PresentationPart part = doc.PresentationPart!;
    List<SlidePart> slides = PptxOutlineReader.SlideParts(part, part.Presentation!);
    SlideLayoutPart target = slides[1].SlideLayoutPart!;
    IReadOnlyList<PptxLayoutMapping> mappings = PptxLayoutMapper.Map(slides[0], 1, target);
    Assert.Equal(2, mappings.Count);
    Assert.All(mappings, mapping => Assert.NotNull(mapping.Target));
    Assert.False(mappings[0].IsStranded);
    Assert.False(mappings[1].IsStranded);
  }

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

using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Deck creation from the pinned markdown subset: the slide split, the title and
///     bullet rules, the widescreen size, the package a deck needs to open with a design,
///     and the refusals that leave nothing behind.</summary>
public sealed class PptxCreateTests
{
  private const string TwoSlideSource = "# Title\n- one\n- two\n---\n# Second\n- three";

  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
  {
    DirectoryInfo dir = TestTempRoot.CreateDirectory("pptx-create");
    return Path.Combine(dir.FullName, name);
  }

  [Fact]
  public async Task Creates_one_slide_per_source_slide_with_its_title_and_first_line()
  {
    string path = await Create(ScratchFile("two.pptx"), TwoSlideSource);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(2, outline.SlideCount);
    Assert.Equal(["Title", "Second"], outline.Slides.Select(slide => slide.Title));
    Assert.Equal(["one", "three"], outline.Slides.Select(slide => slide.FirstLine));
  }

  [Fact]
  public async Task Creates_a_one_slide_deck_when_the_source_has_no_separator()
  {
    string path = await Create(ScratchFile("one.pptx"), "# Only\n- alpha\n- beta");
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(1, outline.SlideCount);
    Assert.Equal("Only", outline.Slides[0].Title);
    Assert.Equal("alpha", outline.Slides[0].FirstLine);
  }

  [Fact]
  public async Task Takes_the_first_non_empty_line_as_the_title_when_it_is_not_a_heading()
  {
    string path = await Create(ScratchFile("plain.pptx"), "\n\nPlain title\n- one");
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal("Plain title", outline.Slides[0].Title);
    Assert.Equal("one", outline.Slides[0].FirstLine);
  }

  [Fact]
  public async Task Creates_a_widescreen_deck()
  {
    string path = await Create(ScratchFile("size.pptx"), TwoSlideSource);
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    P.SlideSize size = doc.PresentationPart!.Presentation!.SlideSize!;
    Assert.Equal(12192000, size.Cx!.Value);
    Assert.Equal(6858000, size.Cy!.Value);
  }

  [Fact]
  public async Task Creates_a_package_with_one_master_one_layout_and_a_theme()
  {
    string path = await Create(ScratchFile("design.pptx"), TwoSlideSource);
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    PresentationPart presentation = doc.PresentationPart!;
    SlideMasterPart master = Assert.Single(presentation.SlideMasterParts);
    _ = Assert.Single(master.SlideLayoutParts);
    Assert.NotNull(master.ThemePart);
    Assert.Same(master, Assert.Single(master.SlideLayoutParts).SlideMasterPart);
    Assert.Equal(2, presentation.SlideParts.Count());
    Assert.Empty(OrphanedEntries(path));
  }

  [Fact]
  public async Task Builds_every_slide_on_the_same_layout()
  {
    string path = await Create(ScratchFile("shared-layout.pptx"), TwoSlideSource);
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    _ = Assert.Single(doc.PresentationPart!.SlideParts.Select(part => part.SlideLayoutPart!.Uri).Distinct());
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(["Title and Content"], outline.LayoutNames);
  }

  [Fact]
  public async Task States_a_bullet_character_for_each_of_the_two_body_levels()
  {
    string path = await Create(ScratchFile("bullets.pptx"), TwoSlideSource);
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlideMasterPart master = Assert.Single(doc.PresentationPart!.SlideMasterParts);
    SlideLayoutPart layout = Assert.Single(master.SlideLayoutParts);
    P.Shape body = layout.SlideLayout!.CommonSlideData!.ShapeTree!.Elements<P.Shape>().ElementAt(1);
    string style = body.TextBody!.ListStyle!.OuterXml;
    Assert.Contains("char=\"\u2022\"", style, StringComparison.Ordinal);
    Assert.Contains("char=\"\u2013\"", style, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Reads_the_created_slide_back_with_its_title_and_body_shapes()
  {
    string path = await Create(ScratchFile("shapes.pptx"), TwoSlideSource);
    PptxSlideSnapshot slide = await ReadSlide(path, 1);
    Assert.Equal(2, slide.Shapes.Count);
    PptxTextBoxShape title = Assert.IsType<PptxTextBoxShape>(slide.Shapes[0]);
    Assert.True(title.IsTitle);
    Assert.Equal("Title", title.Text);
    PptxTextBoxShape body = Assert.IsType<PptxTextBoxShape>(slide.Shapes[1]);
    Assert.Equal("one\ntwo", body.Text);
    Assert.Equal("Title and Content", slide.LayoutName);
    Assert.False(slide.Hidden);
    Assert.Equal(string.Empty, slide.Notes);
  }

  [Fact]
  public async Task Applies_bold_and_italic_markers_to_the_body_runs()
  {
    string path = await Create(ScratchFile("emphasis.pptx"), "# Title\n- plain **bold** *italic*");
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlidePart slidePart = doc.PresentationPart!.SlideParts.First();
    P.Shape body = BodyOf(slidePart);
    List<A.Run> runs = [.. body.TextBody!.Elements<A.Paragraph>().SelectMany(paragraph => paragraph.Elements<A.Run>())];
    Assert.Equal(["plain ", "bold", " ", "italic"], runs.Select(run => run.Text!.Text));
    Assert.True(runs[1].RunProperties!.Bold!.Value);
    Assert.True(runs[3].RunProperties!.Italic!.Value);
    Assert.Null(runs[0].RunProperties);
  }

  [Fact]
  public async Task Indents_a_line_beginning_with_two_spaces_as_a_second_level_bullet()
  {
    string path = await Create(ScratchFile("nested.pptx"), "# Title\n- outer\n  - nested\n\t- tabbed");
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlidePart slidePart = doc.PresentationPart!.SlideParts.First();
    P.Shape body = BodyOf(slidePart);
    List<A.Paragraph> paragraphs = [.. body.TextBody!.Elements<A.Paragraph>()];
    Assert.Equal(3, paragraphs.Count);
    Assert.Equal([0, 1, 1], paragraphs.Select(paragraph => paragraph.ParagraphProperties?.Level?.Value ?? 0));
    Assert.Equal(["outer", "nested", "tabbed"],
        paragraphs.Select(paragraph => paragraph.Elements<A.Run>().First().Text!.Text));
  }

  [Fact]
  public async Task Strips_list_and_heading_markers_from_the_text_they_carry()
  {
    string path = await Create(ScratchFile("markers.pptx"), "## Heading\n1. first\n- second");
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal("Heading", outline.Slides[0].Title);
    Assert.Equal("first", outline.Slides[0].FirstLine);
  }

  [Fact]
  public async Task Reports_a_version_that_a_subsequent_read_confirms()
  {
    string path = ScratchFile("version.pptx");
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, TwoSlideSource,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.IsSuccess ? string.Empty : created.Error.Message);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(created.Value.NewVersion, outline.Version);
  }

  [Fact]
  public async Task Produces_a_deck_a_text_edit_can_be_applied_to()
  {
    string path = await Create(ScratchFile("editable.pptx"), TwoSlideSource);
    PptxOutline outline = await ReadOutline(path);
    Result<PptxEditOutcome> edited = await _access.EditAsync(path, outline.Version,
        [new ReplaceText("s2.sh1", "Rewritten")], TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(edited.IsSuccess, edited.IsSuccess ? string.Empty : edited.Error.Message);
    PptxOutline after = await ReadOutline(path);
    Assert.Equal(["Title", "Rewritten"], after.Slides.Select(slide => slide.Title));
  }

  [Theory]
  [InlineData("")]
  [InlineData("   \n\n  \n")]
  public async Task Refuses_a_source_with_no_text(string source)
  {
    string path = ScratchFile("empty.pptx");
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, source,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsSuccess);
    Assert.Equal(PptxErrorCodes.MarkdownInvalid, created.Error.Code);
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task Refuses_a_source_whose_slide_carries_no_text()
  {
    string path = ScratchFile("blank-slide.pptx");
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, "# One\n---\n\n---\n# Three",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsSuccess);
    Assert.Equal(PptxErrorCodes.MarkdownInvalid, created.Error.Code);
    Assert.Contains("slide 2", created.Error.Message, StringComparison.Ordinal);
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task Refuses_a_source_with_an_unclosed_bold_marker()
  {
    string path = ScratchFile("unclosed.pptx");
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, "# Title\n- one **two",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsSuccess);
    Assert.Equal(PptxErrorCodes.MarkdownInvalid, created.Error.Code);
    Assert.Contains("line 2", created.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Refuses_a_path_that_already_exists_and_leaves_it_unchanged()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("existing.pptx"));
    byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, TwoSlideSource,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, created.Error.Code);
    byte[] after = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(before, after);
  }

  [Fact]
  public async Task Refuses_a_missing_parent_directory_as_file_not_found()
  {
    string directory = Path.Combine(Path.GetDirectoryName(ScratchFile("x.pptx"))!, "absent", "deeper");
    string path = Path.Combine(directory, "deck.pptx");
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, TwoSlideSource,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsSuccess);
    Assert.Equal(PptxErrorCodes.FileNotFound, created.Error.Code);
  }

  private async Task<string> Create(string path, string source)
  {
    Result<PptxSaveOutcome> created = await _access.CreateAsync(path, source,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.IsSuccess ? string.Empty : created.Error.Message);
    return path;
  }

  private async Task<PptxOutline> ReadOutline(string path)
  {
    Result<PptxOutline> outline = await _access.ReadOutlineAsync(path,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outline.IsSuccess, outline.IsSuccess ? string.Empty : outline.Error.Message);
    return outline.Value;
  }

  private async Task<PptxSlideSnapshot> ReadSlide(string path, int slide)
  {
    Result<PptxSlideSnapshot> snapshot = await _access.ReadSlideAsync(path, slide,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(snapshot.IsSuccess, snapshot.IsSuccess ? string.Empty : snapshot.Error.Message);
    return snapshot.Value;
  }

  /// <summary>The body text box of a created slide: the shape after the title.</summary>
  private static P.Shape BodyOf(SlidePart slidePart)
    => slidePart.Slide!.CommonSlideData!.ShapeTree!.Elements<P.Shape>().ElementAt(1);

  /// <summary>Every zip entry that is not reachable from the presentation part, which is
  ///     what PowerPoint reports as a repair.</summary>
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
            && !entry.StartsWith("/docProps/", StringComparison.Ordinal)
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
}

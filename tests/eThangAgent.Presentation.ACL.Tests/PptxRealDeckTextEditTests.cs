using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Text edits against the real 41-slide teaching deck: read in place, mutated only
///     on a temporary copy that is deleted afterwards. The deck is licensed material
///     belonging to another project, so it is a verification input only.</summary>
[Trait("Category", "OptIn")]
public sealed class PptxRealDeckTextEditTests
{
  private const string TeachingDeck = PptxRealDeck.TeachingDeckPath;

  private readonly DirectPptxAccess _access = new();

  [Fact]
  public async Task Reads_the_teaching_deck_outline_with_every_slide_and_its_layouts()
  {
    PptxRealDeck.Teaching();
    PptxOutline outline = await Outline(TeachingDeck);

    Assert.Equal(41, outline.SlideCount);
    Assert.Equal(41, outline.Slides.Count);
    Assert.Equal(19, outline.Media.Count);
    Assert.Equal(["TITLE", "OBJECT"], outline.LayoutNames);
    Assert.Equal(Enumerable.Range(1, 41), outline.Slides.Select(s => s.Number));
    Assert.All(outline.Slides, s => Assert.False(string.IsNullOrWhiteSpace(s.LayoutName)));
  }

  /// <summary>Every slide's notes read back as text rather than failing, which is what makes
  ///     a notes edit on a real deck safe: the parts exist on all 41 slides.</summary>
  [Fact]
  public async Task Reads_the_notes_of_every_slide_in_the_teaching_deck()
  {
    PptxRealDeck.Teaching();
    PptxOutline outline = await Outline(TeachingDeck);

    foreach (PptxSlideSummary summary in outline.Slides)
    {
      Result<PptxSlideSnapshot> slide = await _access.ReadSlideAsync(TeachingDeck, summary.Number,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(slide.IsSuccess, slide.IsSuccess ? string.Empty : slide.Error.Message);
      Assert.NotNull(slide.Value.Notes);
    }
  }

  /// <summary>The fidelity bar on a real deck: one title replaced, everything the edit did
  ///     not address still there - 41 slides, every slide's notes, all 19 media parts, the
  ///     layouts, and no orphaned part in the package.</summary>
  [Fact]
  public async Task Replacing_one_title_leaves_every_other_slide_note_and_media_part_intact()
  {
    PptxRealDeck.Teaching();
    string copy = Copy();
    try
    {
      PptxOutline before = await Outline(copy);
      PptxSlideSnapshot slide2 = await Slide(copy, 2);
      PptxTextBoxShape title = Assert.Single(slide2.Shapes.OfType<PptxTextBoxShape>(), s => s.IsTitle);
      List<string> notesBefore = await Notes(copy, before.SlideCount);

      Result<PptxEditOutcome> edit = await _access.EditAsync(copy, before.Version,
          [new ReplaceText(title.Address, "Chapter 1: Mobile Devices II")],
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(edit.IsSuccess, edit.IsSuccess ? string.Empty : edit.Error.Message);
      Assert.Equal(1, edit.Value.ChangedCount);

      PptxOutline after = await Outline(copy);
      Assert.Equal(41, after.SlideCount);
      Assert.Equal("Chapter 1: Mobile Devices II", after.Slides[1].Title);
      Assert.Equal(before.Slides[0].Title, after.Slides[0].Title);
      Assert.Equal(before.Slides[2].Title, after.Slides[2].Title);
      Assert.Equal(before.Media.Count, after.Media.Count);
      Assert.Equal(19, after.Media.Count);
      Assert.Equal(before.LayoutNames, after.LayoutNames);
      Assert.Equal(notesBefore, await Notes(copy, after.SlideCount));
      Assert.Empty(OrphanedEntries(copy));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  private static string Copy()
  {
    string copy = Path.Combine(TestTempRoot.Root, $"teaching-{Guid.NewGuid():N}.pptx");
    File.Copy(TeachingDeck, copy);
    return copy;
  }

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> outline = await _access.ReadOutlineAsync(path,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outline.IsSuccess, outline.IsSuccess ? string.Empty : outline.Error.Message);
    return outline.Value;
  }

  private async Task<PptxSlideSnapshot> Slide(string path, int number)
  {
    Result<PptxSlideSnapshot> slide = await _access.ReadSlideAsync(path, number,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(slide.IsSuccess, slide.IsSuccess ? string.Empty : slide.Error.Message);
    return slide.Value;
  }

  private async Task<List<string>> Notes(string path, int slideCount)
  {
    List<string> notes = [];
    for (int number = 1; number <= slideCount; number++)
    {
      notes.Add((await Slide(path, number).ConfigureAwait(true)).Notes);
    }

    return notes;
  }

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

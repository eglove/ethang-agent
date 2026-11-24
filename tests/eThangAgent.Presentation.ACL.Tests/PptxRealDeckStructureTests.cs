using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Structural edits against the real 41-slide teaching deck, read in place and
///     mutated only on a temporary copy that is deleted afterwards. The deck is licensed
///     material belonging to another project, so it is a verification input only.</summary>
public sealed class PptxRealDeckStructureTests
{
  private const string TeachingDeck = @"C:\Users\glove\projects\lakewood\intro-to-computers\A+  220-1201 Module 1.pptx";

  private readonly DirectPptxAccess _access = new();

  [Fact]
  public async Task Inserts_a_slide_into_the_teaching_deck_and_keeps_every_other_slide()
  {
    Assert.SkipUnless(File.Exists(TeachingDeck), "the teaching deck is not on this machine");
    string copy = Copy();
    try
    {
      PptxOutline before = await Outline(copy);
      Assert.Equal(41, before.SlideCount);
      PptxEditOutcome outcome = await Edit(copy, new InsertSlide(3, "OBJECT"));
      Assert.Equal(1, outcome.ChangedCount);
      PptxOutline after = await Outline(copy);
      Assert.Equal(42, after.SlideCount);
      Assert.Equal("OBJECT", after.Slides[3].LayoutName);
      Assert.Equal(before.Slides[0].Title, after.Slides[0].Title);
      Assert.Equal(before.Slides[3].Title, after.Slides[4].Title);
      Assert.Equal(before.Slides[40].Title, after.Slides[41].Title);
      Assert.Equal(before.Media.Count, after.Media.Count);
    }
    finally
    {
      File.Delete(copy);
    }
  }

  [Fact]
  public async Task Deletes_a_slide_from_the_teaching_deck_and_leaves_no_orphaned_part()
  {
    Assert.SkipUnless(File.Exists(TeachingDeck), "the teaching deck is not on this machine");
    string copy = Copy();
    try
    {
      PptxOutline before = await Outline(copy);
      _ = await Edit(copy, new DeleteSlide(1));
      PptxOutline after = await Outline(copy);
      Assert.Equal(40, after.SlideCount);
      Assert.Equal(before.Slides[1].Title, after.Slides[0].Title);
      Assert.Empty(OrphanedEntries(copy));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  [Fact]
  public async Task Moves_a_slide_in_the_teaching_deck()
  {
    Assert.SkipUnless(File.Exists(TeachingDeck), "the teaching deck is not on this machine");
    string copy = Copy();
    try
    {
      PptxOutline before = await Outline(copy);
      _ = await Edit(copy, new MoveSlide(1, 3));
      PptxOutline after = await Outline(copy);
      Assert.Equal(41, after.SlideCount);
      Assert.Equal(before.Slides[1].Title, after.Slides[0].Title);
      Assert.Equal(before.Slides[2].Title, after.Slides[1].Title);
      Assert.Equal(before.Slides[0].Title, after.Slides[2].Title);
    }
    finally
    {
      File.Delete(copy);
    }
  }

  [Fact]
  public async Task Changes_a_teaching_deck_slide_from_TITLE_to_OBJECT_and_keeps_its_text()
  {
    Assert.SkipUnless(File.Exists(TeachingDeck), "the teaching deck is not on this machine");
    string copy = Copy();
    try
    {
      PptxSlideSnapshot before = await SlideRead(copy, 1);
      Assert.Equal("TITLE", before.LayoutName);
      _ = await Edit(copy, new SetSlideLayout(1, "OBJECT"));
      PptxSlideSnapshot after = await SlideRead(copy, 1);
      Assert.Equal("OBJECT", after.LayoutName);
      Assert.Equal(before.Shapes.Count, after.Shapes.Count);
      Assert.Equal(TitleOf(before), TitleOf(after));
      Assert.NotEmpty(TitleOf(after));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  [Fact]
  public async Task Refuses_to_move_a_teaching_deck_slide_onto_a_layout_that_would_strand_its_pictures()
  {
    Assert.SkipUnless(File.Exists(TeachingDeck), "the teaching deck is not on this machine");
    string copy = Copy();
    try
    {
      byte[] bytes = await File.ReadAllBytesAsync(copy, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Result<PptxOutline> outline = await _access.ReadOutlineAsync(copy, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(outline.IsSuccess, outline.IsSuccess ? string.Empty : outline.Error.Message);
      Result<PptxEditOutcome> outcome = await _access.EditAsync(copy, outline.Value.Version,
          [new SetSlideLayout(1, "BLANK")], TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.False(outcome.IsSuccess);
      Assert.Equal(PptxErrorCodes.StrandedContent, outcome.Error.Code);
      Assert.Contains("s1.sh4", outcome.Error.Message, StringComparison.Ordinal);
      Assert.Equal(bytes, await File.ReadAllBytesAsync(copy, TestContext.Current.CancellationToken).ConfigureAwait(true));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  private static string TitleOf(PptxSlideSnapshot slide)
    => slide.Shapes.OfType<PptxTextBoxShape>().FirstOrDefault(shape => shape.IsTitle)?.Text ?? string.Empty;

  private static string Copy()
  {
    string path = Path.Combine(TestTempRoot.CreateDirectory("pptx-real").FullName, "teaching.pptx");
    File.Copy(TeachingDeck, path, true);
    return path;
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
    Result<PptxOutline> outline = await _access.ReadOutlineAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outline.IsSuccess, outline.IsSuccess ? string.Empty : outline.Error.Message);
    Result<PptxEditOutcome> outcome = await _access.EditAsync(path, outline.Value.Version, operations,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outcome.IsSuccess, outcome.IsSuccess ? string.Empty : outcome.Error.Message);
    return outcome.Value;
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

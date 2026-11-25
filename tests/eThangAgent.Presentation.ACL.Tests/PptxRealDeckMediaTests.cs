using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The media surface against the real 41-slide teaching deck: 19 media parts across
///     41 slides and 99 picture shapes, read in place and mutated only on a temporary copy
///     that is deleted afterwards. The deck is licensed material belonging to another
///     project, so it is a verification input only.</summary>
[Trait("Category", "OptIn")]
public sealed class PptxRealDeckMediaTests
{
  private const string TeachingDeck = PptxRealDeck.TeachingDeckPath;

  private readonly DirectPptxAccess _access = new();

  [Fact]
  public async Task Extracts_every_media_part_the_outline_lists()
  {
    PptxRealDeck.Teaching();
    PptxOutline outline = await Outline(TeachingDeck);
    Assert.Equal(19, outline.Media.Count);
    Result<IReadOnlyList<ToolResultImage>> extracted =
        await _access.ExtractImagesAsync(TeachingDeck, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(extracted.IsSuccess, extracted.IsSuccess ? string.Empty : extracted.Error.Message);
    Assert.Equal(outline.Media.Count, extracted.Value.Count);
    Assert.Equal(15, extracted.Value.Count(image => image.MediaType == "image/jpeg"));
    Assert.Equal(4, extracted.Value.Count(image => image.MediaType == "image/png"));
  }

  [Fact]
  public async Task Returns_the_bytes_the_deck_carries_for_each_media_part()
  {
    PptxRealDeck.Teaching();
    PptxOutline outline = await Outline(TeachingDeck);
    Result<IReadOnlyList<ToolResultImage>> extracted =
        await _access.ExtractImagesAsync(TeachingDeck, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(extracted.IsSuccess, extracted.IsSuccess ? string.Empty : extracted.Error.Message);
    Dictionary<string, long> byName = outline.Media.ToDictionary(media => media.Name, media => media.ByteCount, StringComparer.Ordinal);
    List<long> sizes = [.. extracted.Value.Select(image => Convert.FromBase64String(image.Base64Data).LongLength)];
    Assert.Equal(byName.Values.Order(), sizes.Order());
  }

  [Fact]
  public async Task Replaces_one_picture_without_changing_a_picture_that_shares_its_media()
  {
    PptxRealDeck.Teaching();
    string copy = Copy();
    try
    {
      PptxOutline before = await Outline(copy);
      (int slide, PptxPictureShape picture) = await SharedPicture(copy);
      byte[] original = MediaBytes(copy, picture.MediaName);
      string source = TestPptxImage.WritePng(Path.Combine(Path.GetDirectoryName(copy)!, "replacement.png"));
      _ = await Edit(copy, new ReplacePicture(picture.Address, source));
      PptxOutline after = await Outline(copy);
      Assert.Equal(before.SlideCount, after.SlideCount);
      Assert.Equal(before.Media.Count + 1, after.Media.Count);
      PptxSlideSnapshot replaced = await SlideRead(copy, slide);
      PptxPictureShape changed = Assert.IsType<PptxPictureShape>(replaced.Shapes[picture.Index - 1]);
      Assert.NotEqual(picture.MediaName, changed.MediaName);
      Assert.Equal(TestPptxImage.PngBytes(), MediaBytes(copy, changed.MediaName));
      Assert.Equal(original, MediaBytes(copy, picture.MediaName));
      Assert.Empty(OrphanedEntries(copy));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  [Fact]
  public async Task Inserts_a_picture_a_table_and_a_text_box_into_the_teaching_deck()
  {
    PptxRealDeck.Teaching();
    string copy = Copy();
    try
    {
      PptxOutline before = await Outline(copy);
      string source = TestPptxImage.WriteJpeg(Path.Combine(Path.GetDirectoryName(copy)!, "insert.jpg"));
      PptxSlideSnapshot slideBefore = await SlideRead(copy, 2);
      _ = await Edit(copy,
          new InsertPicture(2, source, 914400L, 914400L, 1828800L, 1371600L),
          new InsertTable(2, 2, 2, [["a", "b"], ["c", "d"]], 914400L, 3657600L, 3657600L, 1828800L),
          new InsertTextBox(2, "Inserted note", 914400L, 5486400L, 3657600L, 914400L));
      PptxOutline after = await Outline(copy);
      Assert.Equal(before.SlideCount, after.SlideCount);
      Assert.Equal(before.Media.Count + 1, after.Media.Count);
      PptxSlideSnapshot slideAfter = await SlideRead(copy, 2);
      Assert.Equal(slideBefore.Shapes.Count + 3, slideAfter.Shapes.Count);
      PptxTableShape table = Assert.Single(slideAfter.Shapes.OfType<PptxTableShape>());
      Assert.Equal(2, table.Rows);
      Assert.Equal(2, table.Cols);
      Assert.Equal("d", table.Cells[3].Text);
      Assert.Contains(slideAfter.Shapes.OfType<PptxTextBoxShape>(), shape => shape.Text == "Inserted note");
      Assert.Empty(OrphanedEntries(copy));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  /// <summary>A picture whose media part another slide also displays, and the slide it sits
  ///     on. The teaching deck shares media between slides, which is what makes a replace
  ///     without a copy visible on a slide the operation never addressed.</summary>
  private async Task<(int Slide, PptxPictureShape Picture)> SharedPicture(string path)
  {
    Dictionary<string, List<(int Slide, PptxPictureShape Picture)>> byMedia = [];
    PptxOutline outline = await Outline(path).ConfigureAwait(true);
    for (int number = 1; number <= outline.SlideCount; number++)
    {
      PptxSlideSnapshot slide = await SlideRead(path, number).ConfigureAwait(true);
      foreach (PptxPictureShape picture in slide.Shapes.OfType<PptxPictureShape>())
      {
        if (!byMedia.TryGetValue(picture.MediaName, out List<(int, PptxPictureShape)>? users))
        {
          users = [];
          byMedia[picture.MediaName] = users;
        }

        users.Add((number, picture));
      }
    }

    foreach (KeyValuePair<string, List<(int Slide, PptxPictureShape Picture)>> pair in byMedia)
    {
      if (pair.Value.Count > 1)
      {
        return pair.Value[0];
      }
    }

    throw new InvalidOperationException("the teaching deck shares no media part between slides");
  }

  /// <summary>The bytes the package carries for a media name, read straight from the zip
  ///     entry so the assertion is about the stored bytes rather than about any part the
  ///     object model happens to expose.</summary>
  private static byte[] MediaBytes(string path, string mediaName)
  {
    using ZipArchive zip = ZipFile.OpenRead(path);
    ZipArchiveEntry entry = zip.Entries.First(candidate => "/" + candidate.FullName == mediaName);
    using MemoryStream buffer = new();
    using (Stream stream = entry.Open())
    {
      stream.CopyTo(buffer);
    }

    return buffer.ToArray();
  }

  private static string Copy()
  {
    string path = Path.Combine(TestTempRoot.CreateDirectory("pptx-real-media").FullName, "teaching.pptx");
    File.Copy(TeachingDeck, path, true);
    return path;
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
}

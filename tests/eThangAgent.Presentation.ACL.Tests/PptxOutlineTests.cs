using System.IO.Compression;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The outline read: slide order from the id list, titles, first lines, hidden
///     flags, layout names, media and the content-true version.</summary>
public sealed class PptxOutlineTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
  {
    DirectoryInfo dir = TestTempRoot.CreateDirectory("pptx-outline");
    return Path.Combine(dir.FullName, name);
  }

  [Fact]
  public async Task Lists_slides_in_presentation_order_not_part_order()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("ordered.pptx"),
        [
          new TestSlideSpec { Title = "One", Body = "first" },
          new TestSlideSpec { Title = "Two", Body = "second" },
          new TestSlideSpec { Title = "Three", Body = "third" },
        ],
        [1, 2, 0]);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(["Two", "Three", "One"], outline.Slides.Select(slide => slide.Title));
    Assert.Equal([1, 2, 3], outline.Slides.Select(slide => slide.Number));
  }

  [Fact]
  public async Task Carries_each_slides_title_first_line_hidden_flag_and_layout()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("summaries.pptx"),
        [
          new TestSlideSpec { Title = "Chapter 1: Mobile Devices I", Body = "Covers batteries\nand chargers" },
          new TestSlideSpec { Title = "Hidden slide", Body = "not shown", Hidden = true },
        ]);
    PptxOutline outline = await ReadOutline(path);
    PptxSlideSummary first = outline.Slides[0];
    Assert.Equal("Chapter 1: Mobile Devices I", first.Title);
    Assert.Equal("Covers batteries", first.FirstLine);
    Assert.False(first.Hidden);
    Assert.Equal("Title and Content", first.LayoutName);
    Assert.True(outline.Slides[1].Hidden);
    Assert.Equal(2, outline.SlideCount);
  }

  [Fact]
  public async Task Skips_blank_body_lines_when_choosing_the_first_line()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("blank.pptx"),
        [new TestSlideSpec { Title = "Title", Body = "\n\n  Real first line  \nsecond" }]);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal("Real first line", outline.Slides[0].FirstLine);
  }

  [Fact]
  public async Task Reports_empty_title_and_first_line_when_the_slide_has_neither()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("bare.pptx"),
        [new TestSlideSpec { Title = string.Empty, Body = string.Empty }]);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(string.Empty, outline.Slides[0].Title);
    Assert.Equal(string.Empty, outline.Slides[0].FirstLine);
  }

  [Fact]
  public async Task Reports_each_distinct_layout_name_once()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("layouts.pptx"),
        [
          new TestSlideSpec { Title = "A", LayoutName = "Title and Content" },
          new TestSlideSpec { Title = "B", LayoutName = "Title Only" },
          new TestSlideSpec { Title = "C", LayoutName = "Title and Content" },
        ]);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(["Title and Content", "Title Only"], outline.LayoutNames);
    Assert.Equal("Title Only", outline.Slides[1].LayoutName);
  }

  [Fact]
  public async Task Lists_the_decks_embedded_media()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("media.pptx"),
        [new TestSlideSpec { Title = "Picture", WithPicture = true }]);
    PptxOutline outline = await ReadOutline(path);
    PptxMediaInfo media = Assert.Single(outline.Media);
    Assert.Equal("image/png", media.MediaType);
    Assert.True(media.ByteCount > 0);
    Assert.StartsWith("/ppt/media/", media.Name, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Reads_media_when_slides_and_their_notes_reference_each_other()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("cyclic.pptx"),
        [new TestSlideSpec { Title = "Noted", Notes = "speaker notes", WithPicture = true }]);
    PptxOutline outline = await ReadOutline(path);
    _ = Assert.Single(outline.Media);
    Assert.Equal(1, outline.SlideCount);
  }

  [Fact]
  public async Task Carries_the_files_content_true_version()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("version.pptx"));
    FileInfo info = new(path);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal(info.Length, outline.Version.Length);
    Assert.Equal(info.LastWriteTimeUtc, outline.Version.LastWriteTimeUtc);
  }

  [Fact]
  public async Task Reads_a_deck_whose_slides_carry_pictures_tables_charts_and_groups()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("content.pptx"),
        [
          new TestSlideSpec { Title = "All", Body = "body", WithPicture = true, WithTable = true, WithChart = true, WithGroup = true },
        ]);
    PptxOutline outline = await ReadOutline(path);
    Assert.Equal("All", outline.Slides[0].Title);
    Assert.Equal("body", outline.Slides[0].FirstLine);
  }

  [Fact]
  public async Task Refuses_a_package_that_opens_but_has_no_readable_presentation_part()
  {
    string path = ScratchFile("broken.pptx");
    WritePackage(path, (PptxFormatSniffer.PresentationPartName, "not xml at all"));
    Assert.Equal(PptxErrorCodes.CorruptPresentation, await ReadOutlineCode(path));
  }

  [Fact]
  public async Task Refuses_a_word_document_renamed_to_pptx()
  {
    string path = ScratchFile("renamed.pptx");
    WritePackage(path, ("word/document.xml", "<document/>"));
    Assert.Equal(PptxErrorCodes.NotAPptx, await ReadOutlineCode(path));
  }

  [Fact]
  public async Task Refuses_a_missing_file()
  {
    string path = ScratchFile("absent.pptx");
    Assert.Equal(PptxErrorCodes.FileNotFound, await ReadOutlineCode(path));
  }

  private static void WritePackage(string path, params (string Name, string Content)[] parts)
  {
    using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach ((string name, string content) in parts)
    {
      using StreamWriter writer = new(archive.CreateEntry(name).Open());
      writer.Write(content);
    }
  }

  private async Task<PptxOutline> ReadOutline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<string> ReadOutlineCode(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path).ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    return result.Error.Code;
  }
}

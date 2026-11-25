using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Media extraction: every media part as an attachable image, a name filter that
///     selects a subset, and the two refusals - a name the deck does not carry and a media
///     part that is not PNG or JPEG.</summary>
public sealed class PptxExtractImagesTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-extract").FullName, name);

  [Fact]
  public async Task Returns_one_image_per_media_part()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("all.pptx"),
    [
      new TestSlideSpec { Title = "One", WithPicture = true },
      new TestSlideSpec { Title = "Two", WithPicture = true },
      new TestSlideSpec { Title = "Three", WithPicture = true },
    ]);
    IReadOnlyList<ToolResultImage> images = await Extract(path, null);
    Assert.Equal(3, images.Count);
    Assert.All(images, image => Assert.Equal("image/png", image.MediaType));
  }

  [Fact]
  public async Task Returns_one_image_per_media_part_of_a_shared_part()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("shared.pptx"),
    [
      new TestSlideSpec { Title = "One", WithPicture = true },
      new TestSlideSpec { Title = "Two", WithPicture = true },
    ]);
    IReadOnlyList<ToolResultImage> images = await Extract(path, null);
    PptxOutline outline = await Outline(path);
    Assert.Equal(outline.Media.Count, images.Count);
  }

  [Fact]
  public async Task Returns_the_bytes_the_deck_carries()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("bytes.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    ToolResultImage image = Assert.Single(await Extract(path, null));
    Assert.Equal(Convert.FromBase64String(TestPptxFactory.PngBase64), Convert.FromBase64String(image.Base64Data));
  }

  [Fact]
  public async Task Selects_the_subset_a_name_filter_names()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("filter.pptx"),
    [
      new TestSlideSpec { Title = "One", WithPicture = true },
      new TestSlideSpec { Title = "Two", WithPicture = true },
      new TestSlideSpec { Title = "Three", WithPicture = true },
    ]);
    PptxOutline outline = await Outline(path);
    string firstName = Path.GetFileName(outline.Media[0].Name);
    IReadOnlyList<ToolResultImage> images = await Extract(path, [firstName]);
    _ = Assert.Single(images);
    Assert.Equal(Convert.FromBase64String(TestPptxFactory.PngBase64), Convert.FromBase64String(images[0].Base64Data));
  }

  [Fact]
  public async Task Selects_a_part_by_its_package_name()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("package-name.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    PptxOutline outline = await Outline(path);
    IReadOnlyList<ToolResultImage> images = await Extract(path, [outline.Media[0].Name]);
    _ = Assert.Single(images);
  }

  [Fact]
  public async Task Refuses_a_name_the_deck_does_not_carry()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("absent-name.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    string code = await ExtractCode(path, ["media1.png"]);
    Assert.Equal(PptxErrorCodes.ImageNotAttachable, code);
  }

  [Fact]
  public async Task Refuses_a_media_part_that_is_not_png_or_jpeg()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("gif-media.pptx"),
        [new TestSlideSpec { Title = "One", WithPicture = true }]);
    ReplacePngWithGif(path);
    string code = await ExtractCode(path, null);
    Assert.Equal(PptxErrorCodes.ImageNotAttachable, code);
  }

  [Fact]
  public async Task Refuses_a_file_that_is_not_a_deck()
  {
    string path = ScratchFile("not-a-deck.pptx");
    await File.WriteAllTextAsync(path, "not a presentation", TestContext.Current.CancellationToken).ConfigureAwait(true);
    string code = await ExtractCode(path, null);
    Assert.Equal(PptxErrorCodes.NotAPptx, code);
  }

  [Fact]
  public async Task Refuses_a_file_that_does_not_exist()
  {
    string code = await ExtractCode(ScratchFile("absent.pptx"), null);
    Assert.Equal(PptxErrorCodes.FileNotFound, code);
  }

  [Fact]
  public async Task Returns_no_images_for_a_deck_with_no_media()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("no-media.pptx"));
    Assert.Empty(await Extract(path, null));
  }

  /// <summary>Overwrites the deck's only media part with GIF bytes, so a media part that is
  ///     not PNG or JPEG exists in an otherwise healthy deck.</summary>
  private static void ReplacePngWithGif(string path)
  {
    using DocumentFormat.OpenXml.Packaging.PresentationDocument doc =
        DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, true);
    DocumentFormat.OpenXml.Packaging.ImagePart part = doc.PresentationPart!.SlideParts.First().ImageParts.First();
    part.FeedData(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("GIF89a")));
    doc.Save();
  }

  private async Task<IReadOnlyList<ToolResultImage>> Extract(string path, IReadOnlyList<string>? names)
  {
    Result<IReadOnlyList<ToolResultImage>> result = await _access.ExtractImagesAsync(path, names).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<string> ExtractCode(string path, IReadOnlyList<string>? names)
  {
    Result<IReadOnlyList<ToolResultImage>> result = await _access.ExtractImagesAsync(path, names).ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    return result.Error.Code;
  }

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }
}

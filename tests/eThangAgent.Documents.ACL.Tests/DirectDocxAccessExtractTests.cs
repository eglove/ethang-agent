using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;


// S3220/S3878 flag the OOXML element-constructor calls in the fixture builders;
// the trees are built exactly this way by design (mirrors TestDocxFactory).
#pragma warning disable S3220, S3878
namespace eThangAgent.Documents.ACL.Tests;

/// <summary>The extract surface (task 11): embedded images as attachable
///     ToolResultImage entries, name filtering, and typed refusals.</summary>
public sealed class DirectDocxAccessExtractTests : IDisposable
{
  private static readonly byte[] Png1x1 = Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

  // Minimal valid JPEG: SOI + EOI markers
  private static readonly byte[] JpegStub = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

  private static readonly byte[] BmpStub = [0x42, 0x4D, 0x00, 0x00, 0x00, 0x00];

  private readonly DirectDocxAccess _access = new();
  private readonly List<string> _dirs = [];

  public void Dispose()
  {
    foreach (string dir in _dirs)
    {
      try
      {
        Directory.Delete(dir, true);
      }
      catch (IOException)
      {
        // best-effort temp cleanup
      }
      catch (UnauthorizedAccessException)
      {
        // best-effort temp cleanup
      }
    }

    GC.SuppressFinalize(this);
  }

  private string TrackDir(string dir)
  {
    _dirs.Add(dir);
    return dir;
  }

  private async Task<IReadOnlyList<ToolResultImage>> ExtractAsync(string path, IReadOnlyList<string>? names = null)
  {
    Result<IReadOnlyList<ToolResultImage>> r = await _access.ExtractImagesAsync(path, names, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  [Fact]
  public async Task Extract_returns_png_as_tool_result_image()
  {
    string path = TestDocxFactory.CreateWithImage("extract-png", Png1x1);
    _ = TrackDir(Path.GetDirectoryName(path)!);
    IReadOnlyList<ToolResultImage> images = await ExtractAsync(path);
    ToolResultImage image = Assert.Single(images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(Png1x1, Convert.FromBase64String(image.Base64Data));
  }

  [Fact]
  public async Task Extract_filters_by_name()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "two.docx");
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      main.Document = new Document(new Body(
        new Paragraph([new Run([new Text("two images")])])));
      ImagePart first = main.AddNewPart<ImagePart>("image/png", "img1");
      using (Stream s = first.GetStream(FileMode.Create))
      {
        await s.WriteAsync(Png1x1, TestContext.Current.CancellationToken);
      }

      ImagePart second = main.AddNewPart<ImagePart>("image/png", "img2");
      using (Stream s = second.GetStream(FileMode.Create))
      {
        await s.WriteAsync(Png1x1, TestContext.Current.CancellationToken);
      }

      doc.Save();
    }

    IReadOnlyList<ToolResultImage> images = await ExtractAsync(path, ["image2.png"]);
    ToolResultImage image = Assert.Single(images);
    Assert.Equal("image/png", image.MediaType);
  }

  [Fact]
  public async Task Extract_unknown_name_refuses()
  {
    string path = TestDocxFactory.CreateWithImage("extract-unknown", Png1x1);
    _ = TrackDir(Path.GetDirectoryName(path)!);
    Result<IReadOnlyList<ToolResultImage>> r = await _access.ExtractImagesAsync(path, ["nope.png"], TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
    Assert.Contains("image", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Extract_skips_non_png_jpeg()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "bmp.docx");
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      main.Document = new Document(new Body(
        new Paragraph([new Run([new Text("with bmp")])])));
      ImagePart bmp = main.AddNewPart<ImagePart>("image/bmp", "img1");
      using (Stream s = bmp.GetStream(FileMode.Create))
      {
        await s.WriteAsync(BmpStub, TestContext.Current.CancellationToken);
      }

      doc.Save();
    }

    IReadOnlyList<ToolResultImage> images = await ExtractAsync(path);
    Assert.Empty(images);
  }

  [Fact]
  public async Task Extract_returns_jpeg()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "jpeg.docx");
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      main.Document = new Document(new Body(
        new Paragraph([new Run([new Text("with jpeg")])])));
      ImagePart jpeg = main.AddNewPart<ImagePart>("image/jpeg", "img1");
      using (Stream s = jpeg.GetStream(FileMode.Create))
      {
        await s.WriteAsync(JpegStub, TestContext.Current.CancellationToken);
      }

      doc.Save();
    }

    IReadOnlyList<ToolResultImage> images = await ExtractAsync(path);
    ToolResultImage image = Assert.Single(images);
    Assert.Equal("image/jpeg", image.MediaType);
  }

  [Fact]
  public async Task Extract_no_images_returns_empty_success()
  {
    string path = TestDocxFactory.CreateSimple("extract-empty");
    _ = TrackDir(Path.GetDirectoryName(path)!);
    IReadOnlyList<ToolResultImage> images = await ExtractAsync(path);
    Assert.Empty(images);
  }
}

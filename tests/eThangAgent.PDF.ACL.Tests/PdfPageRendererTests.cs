using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PDF.ACL.Tests;

/// <summary>The render path (task 8): page -> PNG over PDFtoImage/PDFium, the
///     1-based page-list handling, the open-tail resolution, the 10-page cap, and
///     the typed refusals - all against PDFsharp-generated fixtures.</summary>
public sealed class PdfPageRendererTests : IDisposable
{
  private static readonly int[] PagesOneThree = [1, 3];
  private static readonly int[] PagesOneFour = [1, 4];
  private static readonly int[] PagesEleven = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
  private static readonly int[] PageOne = [1];
  private static readonly int[] PageTwo = [2];

  private readonly DirectPdfAccess _access = new();
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

  private string TempDir()
  {
    string dir = Path.Combine(Path.GetTempPath(), "pdf-render-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    return dir;
  }

  private string NewPath(string name) => Path.Combine(TempDir(), name);

  [Fact]
  public void Render_two_pages_returns_png_images()
  {
    string path = NewPath("three.pdf");
    TestPdfFactory.Create(path, "Page one", "Page two", "Page three");

    Result<PdfRenderOutcome> r = PdfPageRenderer.Render(path, PagesOneThree, PdfLimits.DefaultDpi);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal([1, 3], r.Value.RenderedPages);
    Assert.Equal(2, r.Value.Images.Count);
    Assert.Equal(1, r.Value.Images[0].PageNumber);
    Assert.Equal(3, r.Value.Images[1].PageNumber);
    foreach (PdfPageImage image in r.Value.Images)
    {
      Assert.True(image.PngBytes.Length > 8);
      Assert.Equal(0x89, image.PngBytes[0]);
      Assert.Equal(0x50, image.PngBytes[1]);
      Assert.Equal(0x4E, image.PngBytes[2]);
      Assert.Equal(0x47, image.PngBytes[3]);
      Assert.True(image.WidthPx > 0);
      Assert.True(image.HeightPx > 0);
    }
  }

  [Fact]
  public void ResolvePageRange_open_tail_resolves_against_total()
  {
    Result<IReadOnlyList<int>> two = PdfPageRenderer.ResolvePageRange("2-", 2);
    Assert.True(two.IsSuccess, two.Error?.Message);
    Assert.Equal([2], two.Value);

    Result<IReadOnlyList<int>> three = PdfPageRenderer.ResolvePageRange("2-", 3);
    Assert.True(three.IsSuccess, three.Error?.Message);
    Assert.Equal([2, 3], three.Value);
  }

  [Fact]
  public void Render_page_out_of_range_returns_page_not_found()
  {
    string path = NewPath("range.pdf");
    TestPdfFactory.Create(path, "A", "B", "C");

    Result<PdfRenderOutcome> r = PdfPageRenderer.Render(path, PagesOneFour, PdfLimits.DefaultDpi);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.PageNotFound, r.Error.Code);
    Assert.Contains("3", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_too_many_pages_returns_invalid_page_range()
  {
    string path = NewPath("many.pdf");
    string[] texts = [.. Enumerable.Range(1, 12).Select(i => "page " + i)];
    TestPdfFactory.Create(path, texts);

    Result<PdfRenderOutcome> r = PdfPageRenderer.Render(path, PagesEleven, PdfLimits.DefaultDpi);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.InvalidPageRange, r.Error.Code);
    Assert.Contains("10", r.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  [InlineData(PdfLimits.MaxDpi + 1)]
  public void Render_defensive_dpi_check(int dpi)
  {
    string path = NewPath("dpi.pdf");
    TestPdfFactory.Create(path, "A");

    Result<PdfRenderOutcome> r = PdfPageRenderer.Render(path, PageOne, dpi);
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Render_missing_file_returns_file_not_found()
  {
    string path = Path.Combine(Path.GetTempPath(), "pdf-render-missing-" + Guid.NewGuid().ToString("N") + ".pdf");

    Result<PdfRenderOutcome> r = PdfPageRenderer.Render(path, PageOne, PdfLimits.DefaultDpi);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public void Render_non_pdf_returns_not_a_pdf()
  {
    string path = NewPath("plain.pdf");
    File.WriteAllText(path, "this is not a pdf");

    Result<PdfRenderOutcome> r = PdfPageRenderer.Render(path, PageOne, PdfLimits.DefaultDpi);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.NotAPdf, r.Error.Code);
  }

  [Fact]
  public async Task RenderAsync_delegates_to_renderer()
  {
    string path = NewPath("delegate.pdf");
    TestPdfFactory.Create(path, "A", "B");

    Result<PdfRenderOutcome> r = await _access.RenderAsync(
        path, PageTwo, PdfLimits.DefaultDpi, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal([2], r.Value.RenderedPages);
    _ = Assert.Single(r.Value.Images);
  }
}

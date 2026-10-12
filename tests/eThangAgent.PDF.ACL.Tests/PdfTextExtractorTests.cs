using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PDF.ACL.Tests;

/// <summary>The read path (task 5): per-page text extraction over PdfPig, the
///     content-true version, the 1-based page numbering, and the typed refusals -
///     all against PDFsharp-generated fixtures.</summary>
public sealed class PdfTextExtractorTests : IDisposable
{
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
    string dir = Path.Combine(Path.GetTempPath(), "pdf-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    return dir;
  }

  private string NewPath(string name) => Path.Combine(TempDir(), name);

  [Fact]
  public void Extract_returns_page_slice_with_lines()
  {
    string path = NewPath("three.pdf");
    TestPdfFactory.Create(path, "Page one", "Page two", "Page three");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 0, 2);
    Assert.True(r.IsSuccess, r.Error?.Message);
    PdfSnapshot snap = r.Value;
    Assert.Equal(3, snap.TotalPages);
    Assert.Equal(0, snap.Offset);
    Assert.Equal(2, snap.Pages.Count);
    Assert.Equal(1, snap.Pages[0].PageNumber);
    Assert.Equal(2, snap.Pages[1].PageNumber);
    Assert.Contains("Page one", snap.Pages[0].Lines);
    Assert.Contains("Page two", snap.Pages[1].Lines);
  }

  [Fact]
  public void Extract_reads_last_page_when_offset_is_within_range()
  {
    string path = NewPath("tail.pdf");
    TestPdfFactory.Create(path, "A", "B", "C");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 2, 20);
    Assert.True(r.IsSuccess, r.Error?.Message);
    PdfPageText page = Assert.Single(r.Value.Pages);
    Assert.Equal(3, page.PageNumber);
    Assert.Contains("C", page.Lines);
  }

  [Fact]
  public void Extract_captures_metadata_and_page_dimensions()
  {
    string path = NewPath("meta.pdf");
    TestPdfFactory.CreateWithMetadata(path, "The Title", "The Author", "body");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 0, 20);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal("The Title", r.Value.Title);
    Assert.Equal("The Author", r.Value.Author);
    Assert.True(r.Value.Pages[0].WidthPt > 0);
    Assert.True(r.Value.Pages[0].HeightPt > 0);
  }

  [Fact]
  public void Extract_version_token_round_trips()
  {
    string path = NewPath("version.pdf");
    TestPdfFactory.Create(path, "hello");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 0, 20);
    Assert.True(r.IsSuccess, r.Error?.Message);
    string token = FileVersionToken.Encode(r.Value.Version);
    Result<FileVersion> decoded = FileVersionToken.Decode(token);
    Assert.True(decoded.IsSuccess, decoded.Error?.Message);
    Assert.Equal(r.Value.Version, decoded.Value);
  }

  [Fact]
  public void Extract_offset_at_total_returns_page_not_found()
  {
    string path = NewPath("at-end.pdf");
    TestPdfFactory.Create(path, "A", "B", "C");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 3, 20);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.PageNotFound, r.Error.Code);
    Assert.Contains("3", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Extract_offset_beyond_total_returns_page_not_found()
  {
    string path = NewPath("past-end.pdf");
    TestPdfFactory.Create(path, "A", "B", "C");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 99, 20);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.PageNotFound, r.Error.Code);
  }

  [Fact]
  public void Extract_non_pdf_returns_not_a_pdf()
  {
    string path = NewPath("plain.pdf");
    File.WriteAllText(path, "this is not a pdf");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 0, 20);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.NotAPdf, r.Error.Code);
  }

  [Fact]
  public void Extract_missing_file_returns_file_not_found()
  {
    string path = Path.Combine(Path.GetTempPath(), "pdf-missing-" + Guid.NewGuid().ToString("N") + ".pdf");
    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 0, 20);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public void Extract_encrypted_returns_encrypted()
  {
    string path = NewPath("locked.pdf");
    TestPdfFactory.CreateEncrypted(path, "secret", "hidden");

    Result<PdfSnapshot> r = PdfTextExtractor.Extract(path, 0, 20);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.EncryptedPdf, r.Error.Code);
  }

  [Fact]
  public async Task ReadAsync_refuses_negative_offset()
  {
    string path = NewPath("neg.pdf");
    TestPdfFactory.Create(path, "A");

    Result<PdfSnapshot> r = await _access.ReadAsync(path, -1, 20, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public async Task ReadAsync_refuses_max_pages_out_of_range()
  {
    string path = NewPath("range.pdf");
    TestPdfFactory.Create(path, "A");

    Result<PdfSnapshot> zero = await _access.ReadAsync(path, 0, 0, TestContext.Current.CancellationToken);
    Assert.False(zero.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, zero.Error.Code);

    Result<PdfSnapshot> over = await _access.ReadAsync(path, 0, PdfLimits.MaxPagesPerRead + 1, TestContext.Current.CancellationToken);
    Assert.False(over.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, over.Error.Code);
  }

  [Fact]
  public async Task ReadAsync_delegates_to_extractor()
  {
    string path = NewPath("delegate.pdf");
    TestPdfFactory.Create(path, "A", "B");

    Result<PdfSnapshot> r = await _access.ReadAsync(path, 0, 20, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(2, r.Value.TotalPages);
  }
}

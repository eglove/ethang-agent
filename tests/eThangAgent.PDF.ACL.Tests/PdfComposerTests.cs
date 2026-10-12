using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using UglyToad.PdfPig;

namespace eThangAgent.PDF.ACL.Tests;

/// <summary>The create path (task 6): PdfComposer renders the pinned markdown
///     subset to a PDF via QuestPDF, and DirectPdfAccess.CreateAsync writes a new
///     file with the content-true version and page count - read back with PdfPig.</summary>
public sealed class PdfComposerTests : IDisposable
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
    string dir = Path.Combine(Path.GetTempPath(), "pdf-compose-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    return dir;
  }

  private string NewPath(string name) => Path.Combine(TempDir(), name);

  private const string EveryFeatureMarkdown =
      "# Heading One\n"
      + "\n"
      + "Intro paragraph with **bold** and *italic* text.\n"
      + "\n"
      + "## Section Two\n"
      + "\n"
      + "- alpha bullet\n"
      + "- beta bullet\n"
      + "\n"
      + "### Sub Three\n"
      + "\n"
      + "1. step one\n"
      + "2. step two\n"
      + "\n"
      + "| Col A | Col B |\n"
      + "|---|---|\n"
      + "| cell one | cell two |\n";

  private static string AllText(string path)
  {
    using PdfDocument doc = PdfDocument.Open(path);
    return string.Join("\n", doc.GetPages().Select(p => p.Text));
  }

  [Fact]
  public void Compose_writes_document_with_every_subset_feature()
  {
    string path = NewPath("every.pdf");

    Result<int> r = PdfComposer.Compose(EveryFeatureMarkdown, path);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.True(r.Value >= 1);
    Assert.True(File.Exists(path));

    using PdfDocument doc = PdfDocument.Open(path);
    Assert.True(doc.NumberOfPages >= 1);
    Assert.Contains("Heading One", doc.GetPage(1).Text, StringComparison.Ordinal);
    string all = AllText(path);
    Assert.Contains("cell one", all, StringComparison.Ordinal);
    Assert.Contains("alpha bullet", all, StringComparison.Ordinal);
    Assert.Contains("step one", all, StringComparison.Ordinal);
  }

  [Fact]
  public void Compose_rejects_oversize_markdown()
  {
    string path = NewPath("oversize.pdf");
    string markdown = new('a', PdfLimits.MaxMarkdownChars + 1);

    Result<int> r = PdfComposer.Compose(markdown, path);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.MarkdownInvalid, r.Error.Code);
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task Create_writes_new_file_and_reports_version_and_page_count()
  {
    string path = NewPath("new.pdf");

    Result<PdfSaveOutcome> r = await _access.CreateAsync(path, EveryFeatureMarkdown, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.True(File.Exists(path));
    Assert.True(r.Value.PageCount >= 1);

    FileInfo info = new(path);
    Assert.Equal(info.Length, r.Value.Version.Length);
    Assert.Equal(info.LastWriteTimeUtc, r.Value.Version.LastWriteTimeUtc);
    Assert.Contains("Heading One", AllText(path), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_refuses_existing_target()
  {
    string path = NewPath("exists.pdf");
    TestPdfFactory.Create(path, "already here");

    Result<PdfSaveOutcome> r = await _access.CreateAsync(path, "# new", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.OutputExists, r.Error.Code);
    Assert.Contains(path, r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_missing_directory_returns_file_not_found()
  {
    string path = Path.Combine(TempDir(), "missing-dir", "out.pdf");

    Result<PdfSaveOutcome> r = await _access.CreateAsync(path, "# heading", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public async Task Create_rejects_invalid_markdown()
  {
    string path = NewPath("bad.pdf");

    Result<PdfSaveOutcome> r = await _access.CreateAsync(path, "#### four hashes", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.MarkdownInvalid, r.Error.Code);
    Assert.False(File.Exists(path));
  }
}

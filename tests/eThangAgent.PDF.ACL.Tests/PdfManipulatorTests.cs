using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using UglyToad.PdfPig;

namespace eThangAgent.PDF.ACL.Tests;

/// <summary>The manipulate path (task 7): PdfManipulator applies a validated batch of
///     structural operations to an in-memory working copy and DirectPdfAccess
///     .ManipulateAsync writes it atomically - read back with PdfPig. Every operation
///     is validated against the CURRENT page count BEFORE any mutation, so one invalid
///     op refuses the whole batch with nothing saved.</summary>
public sealed class PdfManipulatorTests : IDisposable
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
    string dir = Path.Combine(Path.GetTempPath(), "pdf-manip-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    return dir;
  }

  private string NewPath(string name) => Path.Combine(TempDir(), name);

  private static FileVersion VersionOf(string path)
  {
    FileInfo info = new(path);
    return new FileVersion(info.Length, info.LastWriteTimeUtc);
  }

  private static string[] Texts(string path)
  {
    using PdfDocument doc = PdfDocument.Open(path);
    return [.. doc.GetPages().Select(p => p.Text.Trim())];
  }

  private static string[] FivePages(string path)
  {
    TestPdfFactory.Create(path, "P1", "P2", "P3", "P4", "P5");
    return ["P1", "P2", "P3", "P4", "P5"];
  }

  [Fact]
  public async Task Manipulate_rotates_pages()
  {
    string path = NewPath("rotate.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new RotatePages([1, 2], 90)], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(5, r.Value.PageCount);
    Assert.Equal(1, r.Value.OperationsApplied);

    using PdfDocument doc = PdfDocument.Open(path);
    Assert.Equal(90, doc.GetPage(1).Rotation.Value);
    Assert.Equal(90, doc.GetPage(2).Rotation.Value);
    Assert.Equal(0, doc.GetPage(3).Rotation.Value);
  }

  [Fact]
  public async Task Manipulate_deletes_pages_leaving_the_right_texts()
  {
    string path = NewPath("delete.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new DeletePages([2, 4])], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(3, r.Value.PageCount);
    Assert.Equal(["P1", "P3", "P5"], Texts(path));
  }

  [Fact]
  public async Task Manipulate_reorders_pages_by_full_permutation()
  {
    string path = NewPath("reorder.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new ReorderPages([5, 4, 3, 2, 1])], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(5, r.Value.PageCount);
    Assert.Equal(["P5", "P4", "P3", "P2", "P1"], Texts(path));
  }

  [Fact]
  public async Task Manipulate_extracts_pages_to_a_new_file_and_leaves_the_target()
  {
    string path = NewPath("extract.pdf");
    _ = FivePages(path);
    string output = Path.Combine(Path.GetDirectoryName(path)!, "extracted.pdf");

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new ExtractPages([1, 3], output)], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(5, r.Value.PageCount);
    Assert.True(File.Exists(output));
    Assert.Equal(["P1", "P3"], Texts(output));
    Assert.Equal(["P1", "P2", "P3", "P4", "P5"], Texts(path));
  }

  [Fact]
  public async Task Manipulate_extract_refuses_existing_output()
  {
    string path = NewPath("extract-exists.pdf");
    _ = FivePages(path);
    string output = Path.Combine(Path.GetDirectoryName(path)!, "extracted.pdf");
    TestPdfFactory.Create(output, "already here");

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new ExtractPages([1], output)], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.OutputExists, r.Error.Code);
    Assert.Contains(output, r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_merges_a_source_document()
  {
    string path = NewPath("merge.pdf");
    _ = FivePages(path);
    string source = Path.Combine(Path.GetDirectoryName(path)!, "source.pdf");
    TestPdfFactory.Create(source, "S1", "S2");

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new Merge([source])], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(7, r.Value.PageCount);
    Assert.Equal(["P1", "P2", "P3", "P4", "P5", "S1", "S2"], Texts(path));
  }

  [Fact]
  public async Task Manipulate_merge_refuses_missing_source_naming_it()
  {
    string path = NewPath("merge-missing.pdf");
    _ = FivePages(path);
    string missing = Path.Combine(Path.GetDirectoryName(path)!, "nowhere.pdf");

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new Merge([missing])], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
    Assert.Contains(missing, r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_watermark_text_is_extractable()
  {
    string path = NewPath("watermark.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new WatermarkText("CONFIDENTIAL", null, null, null)], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(5, r.Value.PageCount);

    using PdfDocument doc = PdfDocument.Open(path);
    Assert.Contains("CONFIDENTIAL", doc.GetPage(1).Text, StringComparison.Ordinal);
    Assert.Contains("CONFIDENTIAL", doc.GetPage(5).Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_sets_document_metadata()
  {
    string path = NewPath("metadata.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new SetMetadata("New Title", "New Author", "New Subject")], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);

    using PdfDocument doc = PdfDocument.Open(path);
    Assert.Equal("New Title", doc.Information.Title);
    Assert.Equal("New Author", doc.Information.Author);
    Assert.Equal("New Subject", doc.Information.Subject);
  }

  [Fact]
  public async Task Manipulate_stale_version_is_refused()
  {
    string path = NewPath("stale.pdf");
    _ = FivePages(path);
    FileVersion current = VersionOf(path);
    FileVersion stale = new(current.Length + 1, current.LastWriteTimeUtc);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, stale, [new RotatePages([1], 90)], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.StaleDocument, r.Error.Code);
  }

  [Fact]
  public async Task Manipulate_one_invalid_op_refuses_the_whole_batch_with_nothing_saved()
  {
    string path = NewPath("atomic.pdf");
    _ = FivePages(path);
    string before = VersionOf(path).Token;

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path),
        [new RotatePages([1], 90), new DeletePages([99])],
        TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.PageNotFound, r.Error.Code);
    Assert.Contains("operations[1]", r.Error.Message, StringComparison.Ordinal);
    Assert.Equal(before, VersionOf(path).Token);
    Assert.Equal(["P1", "P2", "P3", "P4", "P5"], Texts(path));
  }

  [Fact]
  public async Task Manipulate_reorder_requires_a_full_permutation()
  {
    string path = NewPath("bad-order.pdf");
    _ = FivePages(path);
    string before = VersionOf(path).Token;

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new ReorderPages([1, 2, 3, 4, 4])], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.InvalidPageRange, r.Error.Code);
    Assert.Equal(before, VersionOf(path).Token);
  }

  [Fact]
  public async Task Manipulate_validates_against_the_page_count_after_prior_ops()
  {
    string path = NewPath("sequential.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path),
        [new DeletePages([5]), new RotatePages([5], 90)],
        TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.PageNotFound, r.Error.Code);
    Assert.Contains("operations[1]", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_applies_a_mixed_batch_in_sequence()
  {
    string path = NewPath("mixed.pdf");
    _ = FivePages(path);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path),
        [new DeletePages([5]), new RotatePages([1], 180), new SetMetadata("T", null, null)],
        TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(4, r.Value.PageCount);
    Assert.Equal(3, r.Value.OperationsApplied);

    using PdfDocument doc = PdfDocument.Open(path);
    Assert.Equal(180, doc.GetPage(1).Rotation.Value);
    Assert.Equal("T", doc.Information.Title);
  }

  [Fact]
  public async Task Manipulate_missing_file_is_file_not_found()
  {
    string path = Path.Combine(Path.GetTempPath(), "pdf-missing-" + Guid.NewGuid().ToString("N") + ".pdf");

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, new FileVersion(1, DateTime.UtcNow), [new RotatePages([1], 90)], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public async Task Manipulate_non_pdf_is_refused_by_the_sniffer()
  {
    string path = NewPath("plain.pdf");
    await File.WriteAllTextAsync(path, "this is not a pdf at all", TestContext.Current.CancellationToken);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new RotatePages([1], 90)], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.NotAPdf, r.Error.Code);
  }
}

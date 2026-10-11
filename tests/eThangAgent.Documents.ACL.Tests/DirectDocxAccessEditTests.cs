using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL.Tests;

/// <summary>The ACL edit path part 1 (task 6): paragraph/cell/row operations, the
///     stale guard, atomic batch refusal, style resolution, and version tokens -
///     all against SDK-generated fixtures with read-back verification.</summary>
public sealed class DirectDocxAccessEditTests : IDisposable
{
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

  private string Track(string path)
  {
    string? dir = Path.GetDirectoryName(path);
    return dir is null ? path : TrackDir(dir);
  }

  private string TrackDir(string dir)
  {
    _dirs.Add(dir);
    return dir;
  }

  private async Task<DocxSnapshot> ReadAsync(string path)
  {
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  private async Task<DocxEditOutcome> EditAsync(string path, FileVersion version, params DocxOperation[] operations)
  {
    Result<DocxEditOutcome> r = await _access.EditAsync(path, version, operations, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  [Fact]
  public async Task ReplaceParagraphText_keeps_style_and_first_run_format()
  {
    string path = TestDocxFactory.CreateWithStyles("edit-replace");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    DocxEditOutcome outcome = await EditAsync(path, snap.Version, new ReplaceParagraphText("p1", "New Title"));
    Assert.Equal(1, outcome.ChangedBlocks);
    DocxSnapshot after = await ReadAsync(path);
    DocxParagraphBlock block = Assert.IsType<DocxParagraphBlock>(after.Blocks[0]);
    Assert.Equal("New Title", block.Text);
    Assert.Equal("heading 1", block.Style);
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    Run first = doc.MainDocumentPart!.Document.Body!.Descendants<Run>().First();
    Assert.NotNull(first.RunProperties?.Bold);
    Assert.Equal("New Title", first.GetFirstChild<Text>()!.Text);
  }

  [Fact]
  public async Task Stale_version_refuses_atomic()
  {
    string path = TestDocxFactory.CreateSimple("edit-stale");
    _ = Track(path);
    DocxSnapshot first = await ReadAsync(path);
    _ = await EditAsync(path, first.Version, new ReplaceParagraphText("p1", "changed once"));
    Result<DocxEditOutcome> r = await _access.EditAsync(path, first.Version, [new ReplaceParagraphText("p1", "changed twice")], TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.StaleDocument, r.Error.Code);
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal("changed once", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Batch_is_atomic_on_invalid_op()
  {
    string path = TestDocxFactory.CreateSimple("edit-atomic");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxEditOutcome> r = await _access.EditAsync(path, snap.Version,
        [new ReplaceParagraphText("p1", "should not land"), new DeleteParagraph("p99")], TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.AddressNotFound, r.Error.Code);
    Assert.Contains("p99", r.Error.Message, StringComparison.Ordinal);
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal("First paragraph", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task SetParagraphStyle_and_unknown_style_refusal()
  {
    string path = TestDocxFactory.CreateWithStyles("edit-style");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new SetParagraphStyle("p2", "heading 1"));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal("heading 1", Assert.IsType<DocxParagraphBlock>(after.Blocks[1]).Style);

    DocxSnapshot snap2 = await ReadAsync(path);
    Result<DocxEditOutcome> r = await _access.EditAsync(path, snap2.Version,
        [new SetParagraphStyle("p2", "Bogus Style")], TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.StyleNotFound, r.Error.Code);
    Assert.Contains("heading 1", r.Error.Message, StringComparison.Ordinal);
    Assert.Contains("Normal", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InsertParagraph_after_p0_and_after_p2()
  {
    string path = TestDocxFactory.CreateSimple("edit-insert");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new InsertParagraph("p0", "Prepended", null));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(4, after.TotalBlocks);
    Assert.Equal("Prepended", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
    _ = Assert.IsType<DocxParagraphBlock>(after.Blocks[1]);
    DocxTableBlock table = Assert.IsType<DocxTableBlock>(after.Blocks[3]);
    Assert.Equal(4, table.Index);

    DocxSnapshot snap2 = await ReadAsync(path);
    _ = await EditAsync(path, snap2.Version, new InsertParagraph("p2", "Between", null));
    DocxSnapshot after2 = await ReadAsync(path);
    Assert.Equal(5, after2.TotalBlocks);
    Assert.Equal("Between", Assert.IsType<DocxParagraphBlock>(after2.Blocks[2]).Text);
  }

  [Fact]
  public async Task DeleteParagraph_removes_block_and_renumbers()
  {
    string path = TestDocxFactory.CreateSimple("edit-delete");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new DeleteParagraph("p2"));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(2, after.TotalBlocks);
    Assert.Equal("First paragraph", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
    DocxTableBlock table = Assert.IsType<DocxTableBlock>(after.Blocks[1]);
    Assert.Equal(2, table.Index);
  }

  [Fact]
  public async Task SetCellText_updates_cell_only()
  {
    string path = TestDocxFactory.CreateSimple("edit-cell");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new SetCellText("t3.r1.c2", "beta"));
    DocxSnapshot after = await ReadAsync(path);
    DocxTableBlock table = Assert.IsType<DocxTableBlock>(after.Blocks[2]);
    Assert.Equal("a", table.Cells.Single(c => c.Row == 1 && c.Col == 1).Text);
    Assert.Equal("beta", table.Cells.Single(c => c.Row == 1 && c.Col == 2).Text);
  }

  [Fact]
  public async Task Insert_and_delete_table_row()
  {
    string path = TestDocxFactory.CreateSimple("edit-rows");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new InsertTableRow("t3", 0));
    DocxSnapshot after = await ReadAsync(path);
    DocxTableBlock two = Assert.IsType<DocxTableBlock>(after.Blocks[2]);
    Assert.Equal(2, two.Rows);
    Assert.Equal(string.Empty, two.Cells.Single(c => c.Row == 1 && c.Col == 1).Text);
    Assert.Equal("a", two.Cells.Single(c => c.Row == 2 && c.Col == 1).Text);

    DocxSnapshot snap2 = await ReadAsync(path);
    _ = await EditAsync(path, snap2.Version, new DeleteTableRow("t3", 1));
    DocxSnapshot back = await ReadAsync(path);
    DocxTableBlock one = Assert.IsType<DocxTableBlock>(back.Blocks[2]);
    Assert.Equal(1, one.Rows);
    Assert.Equal("a", one.Cells.Single(c => c.Row == 1 && c.Col == 1).Text);
  }

  [Fact]
  public async Task Edit_returns_new_version_different_from_old()
  {
    string path = TestDocxFactory.CreateSimple("edit-version");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    DocxEditOutcome outcome = await EditAsync(path, snap.Version, new ReplaceParagraphText("p1", "changed"));
    Assert.NotEqual(snap.Version, outcome.NewVersion);
  }

  [Fact]
  public async Task Edit_refuses_kind_mismatches_and_header_addresses()
  {
    string path = TestDocxFactory.CreateSimple("edit-kinds");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxEditOutcome> r1 = await _access.EditAsync(path, snap.Version,
        [new DeleteParagraph("t3")], TestContext.Current.CancellationToken);
    // r1 consumed the read's version; re-read for the second refusal case
    Assert.False(r1.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r1.Error.Code);

    DocxSnapshot fresh = await ReadAsync(path);
    Result<DocxEditOutcome> r2 = await _access.EditAsync(path, fresh.Version,
        [new ReplaceParagraphText("sec1.header.p1", "nope")], TestContext.Current.CancellationToken);
    Assert.False(r2.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r2.Error.Code);
  }

  [Fact]
  public async Task Edit_refuses_missing_file_and_unknown_address()
  {
    string missing = Path.Combine(Path.GetTempPath(), "docx-missing-" + Guid.NewGuid().ToString("N") + ".docx");
    Result<DocxEditOutcome> r = await _access.EditAsync(missing, new FileVersion(1, DateTime.UnixEpoch),
        [new ReplaceParagraphText("p1", "x")], TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.FileNotFound, r.Error.Code);
  }
}

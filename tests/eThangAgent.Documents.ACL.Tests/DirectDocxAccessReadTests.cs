using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL.Tests;

/// <summary>The ACL read path (task 5): block numbering by kind, pagination, style
///     names, tracked-change/comment counts, media, header/footer layers, the
///     content-true version, and the typed refusals - all against SDK-generated
///     fixtures.</summary>
public sealed class DirectDocxAccessReadTests : IDisposable
{
  private static readonly byte[] Png1x1 = Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

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

  private string TempFile(string name, string content)
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, name);
    File.WriteAllText(path, content);
    return path;
  }

  [Fact]
  public async Task Read_numbers_paragraphs_and_tables_by_kind()
  {
    string path = TestDocxFactory.CreateSimple("read-num");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxSnapshot snap = r.Value;
    Assert.Equal(3, snap.TotalBlocks);
    _ = Assert.IsType<DocxParagraphBlock>(snap.Blocks[0]);
    _ = Assert.IsType<DocxParagraphBlock>(snap.Blocks[1]);
    DocxTableBlock table = Assert.IsType<DocxTableBlock>(snap.Blocks[2]);
    Assert.Equal(3, table.Index);
    Assert.Equal(1, table.Rows);
    Assert.Equal(2, table.Cols);
    Assert.Equal("a", table.Cells.Single(c => c.Row == 1 && c.Col == 1).Text);
    Assert.Equal("b", table.Cells.Single(c => c.Row == 1 && c.Col == 2).Text);
  }

  [Fact]
  public async Task Read_paginates()
  {
    string path = TestDocxFactory.CreateSimple("read-page");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 1, 1, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxSnapshot snap = r.Value;
    Assert.Equal(1, snap.Offset);
    Assert.Equal(3, snap.TotalBlocks);
    DocxParagraphBlock block = Assert.IsType<DocxParagraphBlock>(snap.Blocks.Single());
    Assert.Equal(2, block.Index);
  }

  [Fact]
  public async Task Read_offset_beyond_total_returns_empty_page()
  {
    string path = TestDocxFactory.CreateSimple("read-past-end");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 99, 200, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Empty(r.Value.Blocks);
    Assert.Equal(3, r.Value.TotalBlocks);
  }

  [Fact]
  public async Task Read_reports_style_names()
  {
    string path = TestDocxFactory.CreateWithStyles("read-styles");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxParagraphBlock first = Assert.IsType<DocxParagraphBlock>(r.Value.Blocks[0]);
    DocxParagraphBlock second = Assert.IsType<DocxParagraphBlock>(r.Value.Blocks[1]);
    Assert.Equal("heading 1", first.Style);
    Assert.Equal(string.Empty, second.Style);
  }

  [Fact]
  public async Task Read_counts_tracked_changes_and_comments()
  {
    string changes = TestDocxFactory.CreateWithTrackedChanges("read-changes", "Ethan");
    _ = Track(changes);
    Result<DocxSnapshot> r1 = await _access.ReadAsync(changes, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r1.IsSuccess, r1.Error?.Message);
    Assert.Equal(2, r1.Value.TrackedChangeCount);

    string comments = TestDocxFactory.CreateWithComments("read-comments", "Ethan", "check this");
    _ = Track(comments);
    Result<DocxSnapshot> r2 = await _access.ReadAsync(comments, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r2.IsSuccess, r2.Error?.Message);
    Assert.Equal(1, r2.Value.CommentCount);
  }

  [Fact]
  public async Task Read_lists_media()
  {
    string path = TestDocxFactory.CreateWithImage("read-media", Png1x1);
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxMediaInfo media = Assert.Single(r.Value.Media);
    Assert.Equal("image/png", media.MediaType);
    Assert.EndsWith(".png", media.Name, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(Png1x1.Length, media.ByteCount);
  }

  [Fact]
  public async Task Read_walks_header_and_footer()
  {
    string path = TestDocxFactory.CreateWithHeaderFooter("read-hf");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxSnapshot snap = r.Value;
    Assert.Equal(1, snap.SectionCount);
    DocxSectionLayers section = snap.Sections.Single();
    Assert.Equal(1, section.Section);
    DocxParagraphBlock header = Assert.Single(section.HeaderParagraphs);
    Assert.Equal("Header text", header.Text);
    DocxParagraphBlock footer = Assert.Single(section.FooterParagraphs);
    Assert.Equal("Footer text", footer.Text);
  }

  [Fact]
  public async Task Read_returns_content_true_version()
  {
    string path = TestDocxFactory.CreateSimple("read-version");
    _ = Track(path);
    FileInfo stat = new(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(stat.Length, r.Value.Version.Length);
    Assert.True(Math.Abs((stat.LastWriteTimeUtc - r.Value.Version.LastWriteTimeUtc).TotalSeconds) < 1,
        $"version ticks {r.Value.Version.LastWriteTimeUtc:O} vs stat {stat.LastWriteTimeUtc:O}");
  }

  [Fact]
  public async Task Read_refuses_non_docx()
  {
    string path = TempFile("plain.docx", "hello");
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.NotADocx, r.Error.Code);
  }

  [Fact]
  public async Task Read_refuses_negative_offset()
  {
    string path = TestDocxFactory.CreateSimple("read-neg");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, -1, 200, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public async Task Read_refuses_non_positive_max_blocks()
  {
    string path = TestDocxFactory.CreateSimple("read-zero");
    _ = Track(path);
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 0, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }
}

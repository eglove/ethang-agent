using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL.Tests;

/// <summary>The create path (task 8): the pinned markdown subset -> a complete new
///     package with styles and numbering parts; typed refusals for violations.</summary>
public sealed class DirectDocxAccessCreateTests : IDisposable
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

  [Fact]
  public async Task Create_headings_bullets_numbers_bold_italic_table()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "created.docx");
    const string md = "# Top\n\n- bullet one\n- bullet two\n\n1. first\n2. second\n\n## Sub\n\nplain **bold** and *it* text\n\n| a | b |\n| 1 | 2 |\n";
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, md, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxSnapshot snap = await ReadAsync(path);
    // blocks: heading, bullet, bullet, numbered, numbered, heading, paragraph, table
    Assert.Equal(8, snap.TotalBlocks);
    Assert.Equal("heading 1", Assert.IsType<DocxParagraphBlock>(snap.Blocks[0]).Style);
    Assert.Equal("Top", Assert.IsType<DocxParagraphBlock>(snap.Blocks[0]).Text);
    Assert.Equal("List Paragraph", Assert.IsType<DocxParagraphBlock>(snap.Blocks[1]).Style);
    Assert.Equal("bullet one", Assert.IsType<DocxParagraphBlock>(snap.Blocks[1]).Text);
    Assert.Equal("first", Assert.IsType<DocxParagraphBlock>(snap.Blocks[3]).Text);
    Assert.Equal("heading 2", Assert.IsType<DocxParagraphBlock>(snap.Blocks[5]).Style);
    Assert.Equal("Sub", Assert.IsType<DocxParagraphBlock>(snap.Blocks[5]).Text);
    Assert.Equal("plain bold and it text", Assert.IsType<DocxParagraphBlock>(snap.Blocks[6]).Text);
    DocxTableBlock table = Assert.IsType<DocxTableBlock>(snap.Blocks[7]);
    Assert.Equal(2, table.Rows);
    Assert.Equal("a", table.Cells.Single(c => c.Row == 1 && c.Col == 1).Text);
    Assert.Equal("2", table.Cells.Single(c => c.Row == 2 && c.Col == 2).Text);
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("no main part");
    Assert.NotNull(main.StyleDefinitionsPart);
    Assert.NotNull(main.NumberingDefinitionsPart);
  }

  [Fact]
  public async Task Create_bold_and_italic_become_runs()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "spans.docx");
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, "plain **bold** and *it* end", TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("no main part");
    Body body = main.Document.Body ?? throw new InvalidOperationException("no body");
    List<Run> runs = [.. body.Descendants<Run>()];
    Assert.Equal(5, runs.Count); // plain | bold | " and " | it | " end"
    Assert.NotNull(runs[1].RunProperties?.Bold);
    Assert.Null(runs[2].RunProperties?.Bold);
    Assert.NotNull(runs[3].RunProperties?.Italic);
  }

  [Fact]
  public async Task Create_rejects_heading_level_4()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "h4.docx");
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, "#### deep", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.MarkdownInvalid, r.Error.Code);
    Assert.Contains("line 1", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_rejects_unclosed_bold()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "unclosed.docx");
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, "**x", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.MarkdownInvalid, r.Error.Code);
  }

  [Fact]
  public async Task Create_rejects_existing_path()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "exists.docx");
    await File.WriteAllTextAsync(path, "existing", TestContext.Current.CancellationToken);
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, "# x", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public async Task Create_enforces_markdown_char_cap()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "big.docx");
    string md = string.Create(DocxLimits.MaxMarkdownChars + 1, 'x', (span, c) => span.Fill(c));
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, md, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.MarkdownInvalid, r.Error.Code);
  }

  [Fact]
  public async Task Create_rejects_ragged_table()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "ragged.docx");
    Result<DocxSaveOutcome> r = await _access.CreateAsync(path, "| a | b |\n| 1 |", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.MarkdownInvalid, r.Error.Code);
    Assert.Contains("line 2", r.Error.Message, StringComparison.Ordinal);
  }
}

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;


// S3220/S3878 flag the OOXML element-constructor calls in the fixture builder;
// the trees are built exactly this way by design (mirrors TestDocxFactory).
#pragma warning disable S3220, S3878
namespace eThangAgent.Documents.ACL.Tests;

/// <summary>Edit ops part 2 (task 7): paragraph-granular run formatting and
///     literal case-sensitive find/replace with first-run-format merging.</summary>
public sealed class DirectDocxAccessFormatTests : IDisposable
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

  /// <summary>A paragraph whose text splits "Hello" as bold "He" + plain "llo", plus a
  ///     plain second paragraph - the multi-run merge fixture.</summary>
  private static string CreateSplitRunDoc(string name)
  {
    string dir = Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, name + ".docx");
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      Run bold = new([new RunProperties([new Bold()]), new Text("He") { Space = SpaceProcessingModeValues.Preserve }]);
      Run plain = new([new Text("llo") { Space = SpaceProcessingModeValues.Preserve }]);
      Paragraph hello = new([bold, plain]);
      Paragraph world = new([new Run([new Text("world") { Space = SpaceProcessingModeValues.Preserve }])]);
      main.Document = new Document(new Body(hello, world));
      doc.Save();
    }

    return path;
  }

  [Fact]
  public async Task ApplyRunFormatting_is_paragraph_granular()
  {
    string path = CreateSplitRunDoc("fmt-para");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new ApplyRunFormatting("p1", new DocxRunFormat(true, null, null, null, null)));
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("no main part");
    Body body = main.Document.Body ?? throw new InvalidOperationException("no body");
    Paragraph first = body.Elements<Paragraph>().First();
    List<Run> runs = [.. first.Elements<Run>()];
    Assert.Equal(2, runs.Count);
    Assert.NotNull(runs[0].RunProperties?.Bold);
    Assert.NotNull(runs[1].RunProperties?.Bold);
    Assert.Null(runs[1].RunProperties?.Italic);
  }

  [Fact]
  public async Task ApplyRunFormatting_null_fields_untouched()
  {
    string path = CreateSplitRunDoc("fmt-null");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new ApplyRunFormatting("p1", new DocxRunFormat(true, null, null, null, null)));
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    MainDocumentPart main2 = doc.MainDocumentPart ?? throw new InvalidOperationException("no main part");
    Body body2 = main2.Document.Body ?? throw new InvalidOperationException("no body");
    Run plain = body2.Descendants<Run>().ToList()[1];
    RunProperties? props = plain.RunProperties;
    Assert.NotNull(props?.Bold);
    Assert.Null(props?.FontSize);
    Assert.Null(props?.Color);
  }

  [Fact]
  public async Task ApplyRunFormatting_sets_size_and_color()
  {
    string path = CreateSplitRunDoc("fmt-size");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new ApplyRunFormatting("p2", new DocxRunFormat(null, null, true, 24, "FF0000")));
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    MainDocumentPart main3 = doc.MainDocumentPart ?? throw new InvalidOperationException("no main part");
    Body body3 = main3.Document.Body ?? throw new InvalidOperationException("no body");
    Run run = body3.Descendants<Run>().ToList()[2];
    RunProperties? props2 = run.RunProperties;
    Assert.NotNull(props2?.Underline);
    Assert.NotNull(props2?.FontSize?.Val);
    Assert.Equal("24", props2.FontSize.Val.Value);
    Assert.NotNull(props2?.Color?.Val);
    Assert.Equal("FF0000", props2.Color.Val.Value);
  }

  [Fact]
  public async Task FindReplace_spans_runs_and_merges_first_run_format()
  {
    string path = CreateSplitRunDoc("fmt-fr");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    DocxEditOutcome outcome = await EditAsync(path, snap.Version, new FindReplace("Hello", "Hi"));
    Assert.Equal(1, outcome.ChangedBlocks);
    using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
    Paragraph para = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().First();
    List<Run> runs = [.. para.Elements<Run>()];
    Run hi = Assert.Single(runs);
    Assert.Equal("Hi", hi.GetFirstChild<Text>()!.Text);
    Assert.NotNull(hi.RunProperties?.Bold);
  }

  [Fact]
  public async Task FindReplace_is_literal_and_case_sensitive()
  {
    string path = CreateSplitRunDoc("fmt-literal");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await EditAsync(path, snap.Version, new FindReplace("hello", "bye"));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal("Hello", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);

    DocxSnapshot snap2 = await ReadAsync(path);
    _ = await EditAsync(path, snap2.Version, new FindReplace("a(b", "X"));
    DocxSnapshot after2 = await ReadAsync(path);
    Assert.Equal("Hello", Assert.IsType<DocxParagraphBlock>(after2.Blocks[0]).Text);
  }

  [Fact]
  public async Task FindReplace_reports_changed_blocks()
  {
    string path = CreateSplitRunDoc("fmt-blocks");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    DocxEditOutcome outcome = await EditAsync(path, snap.Version, new FindReplace("o", "0"));
    Assert.Equal(2, outcome.ChangedBlocks);
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal("Hell0", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
    Assert.Equal("w0rld", Assert.IsType<DocxParagraphBlock>(after.Blocks[1]).Text);
  }

  [Fact]
  public async Task FindReplace_no_match_skips_save()
  {
    string path = CreateSplitRunDoc("fmt-nomatch");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    DocxEditOutcome outcome = await EditAsync(path, snap.Version, new FindReplace("zzz", "X"));
    Assert.Equal(0, outcome.ChangedBlocks);
    Assert.Equal(snap.Version, outcome.NewVersion);
  }
}

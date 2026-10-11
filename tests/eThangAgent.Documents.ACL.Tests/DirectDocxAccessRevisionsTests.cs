using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;


// S3220/S3878 flag the OOXML element-constructor calls in the fixture builders;
// the trees are built exactly this way by design (mirrors TestDocxFactory).
#pragma warning disable S3220, S3878
namespace eThangAgent.Documents.ACL.Tests;

/// <summary>The revisions surface (task 10): list with type/author/anchor, accept
///     and reject with scope, and the stale guard.</summary>
public sealed class DirectDocxAccessRevisionsTests : IDisposable
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

  private string NewDoc(string name, Action<DocumentFormat.OpenXml.Packaging.MainDocumentPart> build)
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, name + ".docx");
    using (DocumentFormat.OpenXml.Packaging.WordprocessingDocument doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
    {
      DocumentFormat.OpenXml.Packaging.MainDocumentPart main = doc.AddMainDocumentPart();
      build(main);
      doc.Save();
    }

    return path;
  }

  private static Run RunOf(string text)
    => new([new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }]);

  private async Task<DocxSnapshot> ReadAsync(string path)
  {
    Result<DocxSnapshot> r = await _access.ReadAsync(path, 0, 200, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  private async Task<DocxSaveOutcome> ResolveAsync(string path, FileVersion version, bool accept, DocxRevisionScope scope)
  {
    Result<DocxSaveOutcome> r = await _access.ResolveRevisionsAsync(path, version, accept, scope, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  [Fact]
  public async Task List_reports_type_author_anchor()
  {
    string path = NewDoc("rev-list", main =>
    {
      InsertedRun insertion = new([RunOf("Added")]) { Id = "1", Author = "Ethan", Date = DateTime.UtcNow };
      DeletedRun deletion = new([new Run([new DeletedText("Gone") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }])]) { Id = "2", Author = "Ethan", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([insertion]),
        new Paragraph([deletion])));
    });
    Result<IReadOnlyList<DocxRevisionInfo>> r = await _access.ListRevisionsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(2, r.Value.Count);
    Assert.Equal("Insertion", r.Value[0].Type);
    Assert.Equal("Ethan", r.Value[0].Author);
    Assert.Equal("p1", r.Value[0].AnchorAddress);
    Assert.Equal("Deletion", r.Value[1].Type);
    Assert.Equal("p2", r.Value[1].AnchorAddress);
  }

  [Fact]
  public async Task Accept_insertion_merges_text()
  {
    string path = NewDoc("rev-acc-ins", main =>
    {
      InsertedRun insertion = new([RunOf("Added")]) { Id = "1", Author = "Ethan", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([insertion])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    _ = await ResolveAsync(path, snap.Version, accept: true, new DocxRevisionScope(true, null, null));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(0, after.TrackedChangeCount);
    Assert.Equal("Added", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Reject_deletion_restores_text()
  {
    string path = NewDoc("rev-rej-del", main =>
    {
      DeletedRun deletion = new([new Run([new DeletedText("Gone") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }])]) { Id = "1", Author = "Ethan", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([deletion])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    _ = await ResolveAsync(path, snap.Version, accept: false, new DocxRevisionScope(true, null, null));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(0, after.TrackedChangeCount);
    Assert.Equal("Gone", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Reject_insertion_removes_text()
  {
    string path = NewDoc("rev-rej-ins", main =>
    {
      InsertedRun insertion = new([RunOf("Added")]) { Id = "1", Author = "Ethan", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([insertion])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    _ = await ResolveAsync(path, snap.Version, accept: false, new DocxRevisionScope(true, null, null));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(string.Empty, Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Accept_deletion_removes_text()
  {
    string path = NewDoc("rev-acc-del", main =>
    {
      DeletedRun deletion = new([new Run([new DeletedText("Gone") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }])]) { Id = "1", Author = "Ethan", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([deletion])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    _ = await ResolveAsync(path, snap.Version, accept: true, new DocxRevisionScope(true, null, null));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(0, after.TrackedChangeCount);
    Assert.Equal(string.Empty, Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Scope_by_author()
  {
    string path = NewDoc("rev-author", main =>
    {
      InsertedRun a = new([RunOf("FromA")]) { Id = "1", Author = "A", Date = DateTime.UtcNow };
      InsertedRun b = new([RunOf("FromB")]) { Id = "2", Author = "B", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([a]),
        new Paragraph([b])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    _ = await ResolveAsync(path, snap.Version, accept: true, new DocxRevisionScope(false, "A", null));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(1, after.TrackedChangeCount); // B's insertion remains
    Assert.Equal("FromA", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
    Assert.Equal("FromB", Assert.IsType<DocxParagraphBlock>(after.Blocks[1]).Text);
  }

  [Fact]
  public async Task Scope_by_address()
  {
    string path = NewDoc("rev-addr", main =>
    {
      InsertedRun a = new([RunOf("InP1")]) { Id = "1", Author = "A", Date = DateTime.UtcNow };
      InsertedRun b = new([RunOf("InP2")]) { Id = "2", Author = "A", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([a]),
        new Paragraph([b])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    _ = await ResolveAsync(path, snap.Version, accept: true, new DocxRevisionScope(false, null, "p1"));
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(1, after.TrackedChangeCount);
    Assert.Equal("InP1", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Scope_selecting_nothing_refuses()
  {
    string path = NewDoc("rev-none", main =>
    {
      main.Document = new Document(new Body(
        new Paragraph([RunOf("plain")])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxSaveOutcome> r = await _access.ResolveRevisionsAsync(path, snap.Version, true, new DocxRevisionScope(false, "Nobody", null), TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.RevisionNotFound, r.Error.Code);
  }

  [Fact]
  public async Task Stale_version_refuses_resolve()
  {
    string path = NewDoc("rev-stale", main =>
    {
      InsertedRun insertion = new([RunOf("Added")]) { Id = "1", Author = "Ethan", Date = DateTime.UtcNow };
      main.Document = new Document(new Body(
        new Paragraph([insertion])));
    });
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxSaveOutcome> r = await _access.ResolveRevisionsAsync(path, new FileVersion(snap.Version.Length + 9, snap.Version.LastWriteTimeUtc), true, new DocxRevisionScope(true, null, null), TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.StaleDocument, r.Error.Code);
  }
}

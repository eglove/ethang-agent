using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL.Tests;

/// <summary>The comments surface (task 9): list/add/delete with anchor addresses,
///     marker cleanup on delete, and the stale guard.</summary>
public sealed class DirectDocxAccessCommentsTests : IDisposable
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

  private async Task<DocxSaveOutcome> AddAsync(string path, FileVersion version, string address, string author, string text)
  {
    Result<DocxSaveOutcome> r = await _access.AddCommentAsync(path, version, address, author, text, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  [Fact]
  public async Task List_reports_seeded_comment()
  {
    string path = TestDocxFactory.CreateWithComments("cmt-list", "Ethan", "check this");
    _ = Track(path);
    Result<IReadOnlyList<DocxCommentInfo>> r = await _access.ListCommentsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxCommentInfo comment = Assert.Single(r.Value);
    Assert.Equal(1, comment.Id);
    Assert.Equal("Ethan", comment.Author);
    Assert.Equal("check this", comment.Text);
    Assert.Equal("p1", comment.AnchorAddress);
  }

  [Fact]
  public async Task Add_then_list_round_trips()
  {
    string path = TestDocxFactory.CreateSimple("cmt-add");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    DocxSaveOutcome outcome = await AddAsync(path, snap.Version, "p1", "Ethan", "check this");
    Assert.NotEqual(snap.Version, outcome.NewVersion);
    Result<IReadOnlyList<DocxCommentInfo>> r = await _access.ListCommentsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    DocxCommentInfo comment = Assert.Single(r.Value);
    Assert.Equal(1, comment.Id);
    Assert.Equal("Ethan", comment.Author);
    Assert.Equal("check this", comment.Text);
    Assert.Equal("p1", comment.AnchorAddress);
  }

  [Fact]
  public async Task Add_to_cell_anchors_cell_address()
  {
    string path = TestDocxFactory.CreateSimple("cmt-cell");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await AddAsync(path, snap.Version, "t3.r1.c1", "Ethan", "cell note");
    Result<IReadOnlyList<DocxCommentInfo>> r = await _access.ListCommentsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal("t3.r1.c1", Assert.Single(r.Value).AnchorAddress);
  }

  [Fact]
  public async Task Add_to_header_paragraph_anchors_header()
  {
    string path = TestDocxFactory.CreateWithHeaderFooter("cmt-header");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await AddAsync(path, snap.Version, "sec1.header.p1", "Ethan", "header note");
    Result<IReadOnlyList<DocxCommentInfo>> r = await _access.ListCommentsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal("sec1.header.p1", Assert.Single(r.Value).AnchorAddress);
  }

  [Fact]
  public async Task Add_second_comment_gets_next_id()
  {
    string path = TestDocxFactory.CreateWithComments("cmt-second", "Ethan", "first");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await AddAsync(path, snap.Version, "p1", "Ethan", "second");
    Result<IReadOnlyList<DocxCommentInfo>> r = await _access.ListCommentsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(2, r.Value.Count);
    Assert.Equal(2, r.Value[1].Id);
    Assert.Equal("second", r.Value[1].Text);
  }

  [Fact]
  public async Task Delete_removes_markers_and_reference()
  {
    string path = TestDocxFactory.CreateWithComments("cmt-del", "Ethan", "check this");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    _ = await _access.DeleteCommentAsync(path, snap.Version, 1, TestContext.Current.CancellationToken);
    Result<IReadOnlyList<DocxCommentInfo>> r = await _access.ListCommentsAsync(path, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Empty(r.Value);
    DocxSnapshot after = await ReadAsync(path);
    Assert.Equal(0, after.CommentCount);
    // the anchored paragraph's text survives
    Assert.Equal("Commented text", Assert.IsType<DocxParagraphBlock>(after.Blocks[0]).Text);
  }

  [Fact]
  public async Task Delete_unknown_id_refuses()
  {
    string path = TestDocxFactory.CreateWithComments("cmt-unknown", "Ethan", "check this");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxSaveOutcome> r = await _access.DeleteCommentAsync(path, snap.Version, 99, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.CommentNotFound, r.Error.Code);
  }

  [Fact]
  public async Task Stale_version_refuses_add()
  {
    string path = TestDocxFactory.CreateSimple("cmt-stale");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxSaveOutcome> r = await _access.AddCommentAsync(path, new FileVersion(snap.Version.Length + 5, snap.Version.LastWriteTimeUtc), "p1", "Ethan", "late", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.StaleDocument, r.Error.Code);
  }

  [Fact]
  public async Task Add_to_unknown_address_refuses()
  {
    string path = TestDocxFactory.CreateSimple("cmt-bad");
    _ = Track(path);
    DocxSnapshot snap = await ReadAsync(path);
    Result<DocxSaveOutcome> r = await _access.AddCommentAsync(path, snap.Version, "p99", "Ethan", "nope", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.AddressNotFound, r.Error.Code);
  }
}

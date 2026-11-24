using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Comments across both shapes PowerPoint writes them in: the legacy per-slide
///     comment part and the current one. A read lists both; a write always produces the
///     current shape and leaves legacy comments where they are; a delete removes one
///     comment from whichever shape holds it.</summary>
public sealed class PptxCommentTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-comments").FullName, name);

  [Fact]
  public async Task Lists_two_legacy_comments_with_their_author_text_and_anchor()
  {
    string path = Deck("legacy-two.pptx", new TestSlideSpec { Title = "T", LegacyComments = ["First note", "Second note"] });
    IReadOnlyList<PptxCommentInfo> comments = await Comments(path);
    Assert.Equal(["First note", "Second note"], comments.Select(comment => comment.Text));
    Assert.All(comments, comment => Assert.True(comment.Legacy));
    Assert.All(comments, comment => Assert.Equal("Reviewer", comment.Author));
    Assert.All(comments, comment => Assert.Equal("s1", comment.AnchorAddress));
  }

  [Fact]
  public async Task Lists_a_legacy_and_a_current_comment_together()
  {
    string path = Deck("both.pptx", new TestSlideSpec
    {
      Title = "T",
      LegacyComments = ["Legacy note"],
      CurrentComments = ["Current note"],
    });
    IReadOnlyList<PptxCommentInfo> comments = await Comments(path);
    Assert.Equal(2, comments.Count);
    Assert.True(comments[0].Legacy);
    Assert.Equal("Legacy note", comments[0].Text);
    Assert.False(comments[1].Legacy);
    Assert.Equal("Current note", comments[1].Text);
    Assert.Equal("Reviewer", comments[1].Author);
    Assert.Equal("s1.sh1", comments[1].AnchorAddress);
  }

  [Fact]
  public async Task Lists_comments_across_slides_in_presentation_order()
  {
    string path = Deck("two-slides.pptx",
        new TestSlideSpec { Title = "A", LegacyComments = ["On the first"] },
        new TestSlideSpec { Title = "B", LegacyComments = ["On the second"] });
    IReadOnlyList<PptxCommentInfo> comments = await Comments(path);
    Assert.Equal([1, 2], comments.Select(comment => comment.Id));
    Assert.Equal(["s1", "s2"], comments.Select(comment => comment.AnchorAddress));
  }

  [Fact]
  public async Task Lists_nothing_for_a_deck_without_comments()
  {
    string path = Deck("none.pptx", new TestSlideSpec { Title = "T" });
    Assert.Empty(await Comments(path));
  }

  [Fact]
  public async Task Adds_a_current_comment_anchored_at_the_addressed_shape()
  {
    string path = Deck("add.pptx", new TestSlideSpec { Title = "A" }, new TestSlideSpec { Title = "B" });
    FileVersion version = await Version(path);
    Result<PptxSaveOutcome> added = await _access
        .AddCommentAsync(path, version, "s2.sh1", "Reviewer", "Check this figure", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(added.IsSuccess, added.Error?.Message);
    PptxCommentInfo comment = Assert.Single(await Comments(path));
    Assert.Equal("Reviewer", comment.Author);
    Assert.Equal("Check this figure", comment.Text);
    Assert.Equal("s2.sh1", comment.AnchorAddress);
    Assert.False(comment.Legacy);
  }

  [Fact]
  public async Task Reports_the_added_comment_through_a_slide_read()
  {
    string path = Deck("read-back.pptx", new TestSlideSpec { Title = "A" }, new TestSlideSpec { Title = "B" });
    _ = await Add(path, "s2.sh1", "Reviewer", "Check this figure");
    PptxSlideSnapshot slide = await SlideRead(path, 2);
    PptxCommentInfo comment = Assert.Single(slide.Comments);
    Assert.Equal("Check this figure", comment.Text);
    Assert.Equal("s2.sh1", comment.AnchorAddress);
  }

  [Fact]
  public async Task Adding_to_a_deck_with_legacy_comments_keeps_them()
  {
    string path = Deck("legacy-plus-current.pptx",
        new TestSlideSpec { Title = "T", LegacyComments = ["Legacy note"] });
    _ = await Add(path, "s1.sh1", "Reviewer", "Current note");
    IReadOnlyList<PptxCommentInfo> comments = await Comments(path);
    Assert.Equal(2, comments.Count);
    Assert.True(comments[0].Legacy);
    Assert.Equal("Legacy note", comments[0].Text);
    Assert.False(comments[1].Legacy);
    Assert.Equal("Current note", comments[1].Text);
  }

  [Fact]
  public async Task The_added_comment_is_listed_with_the_version_the_add_reports()
  {
    string path = Deck("version.pptx", new TestSlideSpec { Title = "T" });
    PptxSaveOutcome outcome = await Add(path, "s1.sh1", "Reviewer", "Note");
    Assert.Equal(outcome.NewVersion, await Version(path));
  }

  [Fact]
  public async Task Deletes_exactly_one_legacy_comment_and_leaves_the_other()
  {
    string path = Deck("delete-legacy.pptx",
        new TestSlideSpec { Title = "T", LegacyComments = ["First note", "Second note"] });
    _ = await Delete(path, 1);
    PptxCommentInfo remaining = Assert.Single(await Comments(path));
    Assert.Equal("Second note", remaining.Text);
    Assert.Equal(1, remaining.Id);
  }

  [Fact]
  public async Task Deletes_a_current_comment_and_leaves_the_legacy_one()
  {
    string path = Deck("delete-current.pptx", new TestSlideSpec
    {
      Title = "T",
      LegacyComments = ["Legacy note"],
      CurrentComments = ["Current note"],
    });
    _ = await Delete(path, 2);
    PptxCommentInfo remaining = Assert.Single(await Comments(path));
    Assert.True(remaining.Legacy);
    Assert.Equal("Legacy note", remaining.Text);
  }

  [Fact]
  public async Task Refuses_a_comment_id_that_names_no_comment()
  {
    string path = Deck("missing-id.pptx", new TestSlideSpec { Title = "T" });
    FileVersion version = await Version(path);
    Result<PptxSaveOutcome> deleted = await _access
        .DeleteCommentAsync(path, version, 99, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(deleted.IsSuccess);
    Assert.Equal(PptxErrorCodes.CommentNotFound, deleted.Error.Code);
  }

  [Fact]
  public async Task Refuses_an_add_whose_version_is_stale()
  {
    string path = Deck("stale.pptx", new TestSlideSpec { Title = "T" });
    FileVersion version = await Version(path);
    _ = await Add(path, "s1.sh1", "Reviewer", "First note");
    byte[] before = await Bytes(path).ConfigureAwait(true);
    Result<PptxSaveOutcome> refused = await _access
        .AddCommentAsync(path, version, "s1.sh1", "Reviewer", "Second note", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(refused.IsSuccess);
    Assert.Equal(PptxErrorCodes.StalePresentation, refused.Error.Code);
    Assert.Equal(before, await Bytes(path).ConfigureAwait(true));
  }

  [Fact]
  public async Task Refuses_a_delete_whose_version_is_stale()
  {
    string path = Deck("stale-delete.pptx", new TestSlideSpec { Title = "T", LegacyComments = ["Note"] });
    FileVersion version = await Version(path);
    _ = await Add(path, "s1.sh1", "Reviewer", "Another note");
    byte[] before = await Bytes(path).ConfigureAwait(true);
    Result<PptxSaveOutcome> refused = await _access
        .DeleteCommentAsync(path, version, 1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(refused.IsSuccess);
    Assert.Equal(PptxErrorCodes.StalePresentation, refused.Error.Code);
    Assert.Equal(before, await Bytes(path).ConfigureAwait(true));
  }

  [Fact]
  public async Task Refuses_an_add_anchored_at_a_shape_the_slide_does_not_have()
  {
    string path = Deck("missing-shape.pptx", new TestSlideSpec { Title = "T" });
    FileVersion version = await Version(path);
    byte[] before = await Bytes(path).ConfigureAwait(true);
    Result<PptxSaveOutcome> refused = await _access
        .AddCommentAsync(path, version, "s1.sh9", "Reviewer", "Note", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(refused.IsSuccess);
    Assert.Equal(PptxErrorCodes.AddressNotFound, refused.Error.Code);
    Assert.Equal(before, await Bytes(path).ConfigureAwait(true));
  }

  [Fact]
  public async Task Refuses_an_add_whose_address_does_not_parse()
  {
    string path = Deck("bad-address.pptx", new TestSlideSpec { Title = "T" });
    FileVersion version = await Version(path);
    Result<PptxSaveOutcome> refused = await _access
        .AddCommentAsync(path, version, "slide1", "Reviewer", "Note", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(refused.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, refused.Error.Code);
  }

  [Fact]
  public async Task Reading_a_deck_with_legacy_comments_does_not_modify_the_file()
  {
    string path = Deck("read-only.pptx",
        new TestSlideSpec { Title = "T", LegacyComments = ["First note", "Second note"] });
    byte[] before = await Bytes(path).ConfigureAwait(true);
    _ = await Comments(path);
    _ = await SlideRead(path, 1);
    Assert.Equal(before, await Bytes(path).ConfigureAwait(true));
  }

  [Fact]
  public async Task Refuses_a_file_that_is_not_a_presentation()
  {
    string path = ScratchFile("not-a-deck.pptx");
    await File.WriteAllTextAsync(path, "not a deck", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<IReadOnlyList<PptxCommentInfo>> listed = await _access
        .ListCommentsAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(listed.IsSuccess);
    Assert.Equal(PptxErrorCodes.NotAPptx, listed.Error.Code);
  }

  [Fact]
  public async Task Reads_and_adds_a_comment_on_a_copy_of_the_teaching_deck()
  {
    Assert.SkipUnless(File.Exists(TeachingDeck), "the teaching deck is not on this machine");
    string copy = ScratchFile("teaching-copy.pptx");
    File.Copy(TeachingDeck, copy, true);
    Assert.Empty(await Comments(copy));
    _ = await Add(copy, "s2.sh1", "Reviewer", "Check this figure");
    PptxCommentInfo comment = Assert.Single(await Comments(copy));
    Assert.Equal("s2.sh1", comment.AnchorAddress);
    Assert.Equal(41, (await Outline(copy)).SlideCount);
  }

  private const string TeachingDeck = @"C:\Users\glove\projects\lakewood\intro-to-computers\A+  220-1201 Module 1.pptx";

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private static string Deck(string name, params TestSlideSpec[] slides)
    => TestPptxFactory.CreateDeck(ScratchFile(name), slides);

  private async Task<IReadOnlyList<PptxCommentInfo>> Comments(string path)
  {
    Result<IReadOnlyList<PptxCommentInfo>> result = await _access
        .ListCommentsAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxSaveOutcome> Add(string path, string address, string author, string text)
  {
    FileVersion version = await Version(path).ConfigureAwait(true);
    Result<PptxSaveOutcome> result = await _access
        .AddCommentAsync(path, version, address, author, text, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxSaveOutcome> Delete(string path, int commentId)
  {
    FileVersion version = await Version(path).ConfigureAwait(true);
    Result<PptxSaveOutcome> result = await _access
        .DeleteCommentAsync(path, version, commentId, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxSlideSnapshot> SlideRead(string path, int slide)
  {
    Result<PptxSlideSnapshot> result = await _access
        .ReadSlideAsync(path, slide, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<FileVersion> Version(string path)
  {
    Result<PptxOutline> outline = await _access
        .ReadOutlineAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(outline.IsSuccess, outline.Error?.Message);
    return outline.Value.Version;
  }

  private static Task<byte[]> Bytes(string path)
    => File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
}

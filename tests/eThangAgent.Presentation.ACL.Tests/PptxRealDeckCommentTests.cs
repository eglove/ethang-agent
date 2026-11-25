using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Comments against the real 41-slide teaching deck, read in place and mutated only
///     on a temporary copy that is deleted afterwards. The deck is licensed material
///     belonging to another project, so it is a verification input only. Opt-in because the
///     deck lives on this machine and is not a fixture.</summary>
[Trait("Category", "OptIn")]
public sealed class PptxRealDeckCommentTests
{
  private readonly DirectPptxAccess _access = new();

  [Fact]
  public async Task Reads_and_adds_a_comment_on_a_copy_of_the_teaching_deck()
  {
    PptxRealDeck.Teaching();
    string copy = Path.Combine(TestTempRoot.CreateDirectory("pptx-comments").FullName, "teaching-copy.pptx");
    File.Copy(PptxRealDeck.TeachingDeckPath, copy, true);
    try
    {
      Assert.Empty(await Comments(copy));
      _ = await Add(copy, "s2.sh1", "Reviewer", "Check this figure");
      PptxCommentInfo comment = Assert.Single(await Comments(copy));
      Assert.Equal("s2.sh1", comment.AnchorAddress);
      Assert.Equal(41, (await Outline(copy)).SlideCount);
    }
    finally
    {
      File.Delete(copy);
    }
  }

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

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
    FileVersion version = (await Outline(path).ConfigureAwait(true)).Version;
    Result<PptxSaveOutcome> result = await _access
        .AddCommentAsync(path, version, address, author, text, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }
}

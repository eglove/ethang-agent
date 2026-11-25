using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The animation surface against the small sample deck, which carries a timing
///     element on its single slide: read in place, mutated only on a temporary copy that is
///     deleted afterwards. Opt-in because the deck lives on this machine and is not a fixture.</summary>
[Trait("Category", "OptIn")]
public sealed class PptxRealDeckAnimationTests
{
  private readonly DirectPptxAccess _access = new();

  [Fact]
  public async Task Reads_the_sample_decks_effects_without_error()
  {
    PptxRealDeck.Sample();
    PptxSlideSnapshot slide = await SlideRead(PptxRealDeck.SampleDeckPath, 1);
    Assert.Empty(slide.Animations);
  }

  [Fact]
  public async Task Edits_the_sample_decks_animation_on_a_copy_and_leaves_the_original_alone()
  {
    PptxRealDeck.Sample();
    byte[] original = await Bytes(PptxRealDeck.SampleDeckPath);
    string copy = Path.Combine(TestTempRoot.CreateDirectory("pptx-animation").FullName, "sample.pptx");
    File.Copy(PptxRealDeck.SampleDeckPath, copy);
    try
    {
      _ = await Edit(copy, new SetAnimation(1, 1, "fade s1.sh2", null, null));
      PptxAnimationInfo effect = Assert.Single((await SlideRead(copy, 1)).Animations);
      Assert.Equal("s1.sh2", effect.Address);
      Assert.Equal("entr:10", effect.Effect);
      Assert.Equal("onClick", effect.Trigger);
      Assert.Equal(original, await Bytes(PptxRealDeck.SampleDeckPath));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  private static Task<byte[]> Bytes(string path)
    => File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

  private async Task<PptxEditOutcome> Edit(string path, params PptxOperation[] operations)
  {
    FileVersion version = (await Outline(path).ConfigureAwait(true)).Version;
    Result<PptxEditOutcome> result = await _access
        .EditAsync(path, version, operations, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxSlideSnapshot> SlideRead(string path, int slide)
  {
    Result<PptxSlideSnapshot> result = await _access
        .ReadSlideAsync(path, slide, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }
}

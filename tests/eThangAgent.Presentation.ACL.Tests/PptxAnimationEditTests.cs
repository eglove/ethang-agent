using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The animation edit: the effects a slide reports in playback order, retiming,
///     retriggering and replacing one effect, appending a simple entrance effect, removing
///     one, and the refusals that leave the file byte-identical.</summary>
public sealed class PptxAnimationEditTests
{
  /// <summary>The small sample deck, which carries a timing element on its single slide.
  ///     </summary>
  private const string SampleDeck =
      @"C:\Users\glove\go\pkg\mod\github.com\wailsapp\mimetype@v1.4.1\testdata\pptx.pptx";

  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-animation").FullName, name);

  [Fact]
  public async Task Reports_every_effect_in_playback_order_with_its_target_and_duration()
  {
    string path = Animated("order.pptx", 3);
    IReadOnlyList<PptxAnimationInfo> effects = (await SlideRead(path, 1)).Animations;
    Assert.Equal([1, 2, 3], effects.Select(effect => effect.Index));
    Assert.All(effects, effect => Assert.Equal("s1.sh1", effect.Address));
    Assert.All(effects, effect => Assert.Equal("entr:10", effect.Effect));
    Assert.All(effects, effect => Assert.Equal("onClick", effect.Trigger));
    Assert.Equal(["100", "200", "300"], effects.Select(effect => effect.Duration));
  }

  [Fact]
  public async Task Retimes_one_effect_and_leaves_the_others_alone()
  {
    string path = Animated("retime.pptx", 3);
    PptxEditOutcome outcome = await Edit(path, new SetAnimation(1, 1, null, null, 2000));
    Assert.Equal(1, outcome.ChangedCount);
    IReadOnlyList<PptxAnimationInfo> effects = (await SlideRead(path, 1)).Animations;
    Assert.Equal(["2000", "200", "300"], effects.Select(effect => effect.Duration));
    Assert.Equal([1, 2, 3], effects.Select(effect => effect.Index));
    Assert.All(effects, effect => Assert.Equal("s1.sh1", effect.Address));
    Assert.All(effects, effect => Assert.Equal("entr:10", effect.Effect));
  }

  [Fact]
  public async Task Changes_one_effects_trigger_and_leaves_its_effect_alone()
  {
    string path = Animated("trigger.pptx", 2);
    _ = await Edit(path, new SetAnimation(1, 2, null, "withPrevious", null));
    IReadOnlyList<PptxAnimationInfo> effects = (await SlideRead(path, 1)).Animations;
    Assert.Equal("onClick", effects[0].Trigger);
    Assert.Equal("withPrevious", effects[1].Trigger);
    Assert.Equal(["100", "200"], effects.Select(effect => effect.Duration));
    Assert.All(effects, effect => Assert.Equal("entr:10", effect.Effect));
  }

  [Fact]
  public async Task Replaces_the_effect_on_an_existing_index()
  {
    string path = Animated("replace.pptx", 2);
    _ = await Edit(path, new SetAnimation(1, 1, "wipe", null, null));
    IReadOnlyList<PptxAnimationInfo> effects = (await SlideRead(path, 1)).Animations;
    Assert.Equal(["entr:22", "entr:10"], effects.Select(effect => effect.Effect));
    Assert.Equal(["100", "200"], effects.Select(effect => effect.Duration));
  }

  [Fact]
  public async Task Adds_a_simple_entrance_effect_one_past_the_last_effect()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("add.pptx"),
        [new TestSlideSpec { Title = "T", Body = "B", AnimationEffects = 3 }]);
    PptxEditOutcome outcome = await Edit(path, new SetAnimation(1, 4, "fade s1.sh2", null, null));
    Assert.Equal(1, outcome.ChangedCount);
    IReadOnlyList<PptxAnimationInfo> effects = (await SlideRead(path, 1)).Animations;
    Assert.Equal(4, effects.Count);
    Assert.Equal("s1.sh1", effects[0].Address);
    Assert.Equal("s1.sh2", effects[3].Address);
    Assert.Equal("entr:10", effects[3].Effect);
    Assert.Equal("onClick", effects[3].Trigger);
  }

  [Fact]
  public async Task Adds_an_effect_to_a_slide_that_carries_no_timing_element()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("add-first.pptx"));
    Assert.DoesNotContain("p:timing", SlideXml(path, 1), StringComparison.Ordinal);
    _ = await Edit(path, new SetAnimation(1, 1, "appear s1.sh2", null, 750));
    PptxAnimationInfo effect = Assert.Single((await SlideRead(path, 1)).Animations);
    Assert.Equal("s1.sh2", effect.Address);
    Assert.Equal("entr:1", effect.Effect);
    Assert.Equal("750", effect.Duration);
    Assert.Contains("p:timing", SlideXml(path, 1), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Removes_one_effect_and_renumbers_the_following_ones()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("remove.pptx"),
    [
      new TestSlideSpec { Title = "One" },
      new TestSlideSpec { Title = "Two", AnimationEffects = 3 },
    ]);
    PptxEditOutcome outcome = await Edit(path, new RemoveAnimation(2, 2));
    Assert.Equal(1, outcome.ChangedCount);
    IReadOnlyList<PptxAnimationInfo> effects = (await SlideRead(path, 2)).Animations;
    Assert.Equal([1, 2], effects.Select(effect => effect.Index));
    Assert.Equal(["100", "300"], effects.Select(effect => effect.Duration));
    Assert.Empty((await SlideRead(path, 1)).Animations);
  }

  [Fact]
  public async Task Removing_the_last_effect_leaves_no_timing_element_behind()
  {
    string path = Animated("remove-last.pptx", 1);
    _ = await Edit(path, new RemoveAnimation(1, 1));
    Assert.Empty((await SlideRead(path, 1)).Animations);
    Assert.DoesNotContain("p:timing", SlideXml(path, 1), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Keeps_the_timing_element_while_an_effect_remains()
  {
    string path = Animated("remove-one.pptx", 2);
    _ = await Edit(path, new RemoveAnimation(1, 1));
    PptxAnimationInfo effect = Assert.Single((await SlideRead(path, 1)).Animations);
    Assert.Equal("200", effect.Duration);
    Assert.Contains("p:timing", SlideXml(path, 1), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Refuses_an_effect_index_beyond_one_past_the_last_effect()
  {
    string path = Animated("index-high.pptx", 3);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new SetAnimation(1, 9, "fade", null, null));
    Assert.Equal(PptxErrorCodes.AnimationNotFound, code);
    Assert.Equal(before, await Bytes(path));
    Assert.Equal(3, (await SlideRead(path, 1)).Animations.Count);
  }

  [Fact]
  public async Task Refuses_an_effect_index_below_one()
  {
    string path = Animated("index-low.pptx", 2);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new RemoveAnimation(1, 0));
    Assert.Equal(PptxErrorCodes.AnimationNotFound, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_motion_path_and_states_what_is_supported()
  {
    string path = Animated("motion.pptx", 2);
    byte[] before = await Bytes(path);
    FileVersion version = (await Outline(path).ConfigureAwait(true)).Version;
    Result<PptxEditOutcome> result = await _access
        .EditAsync(path, version, [new SetAnimation(1, 1, "motion path", null, null)],
            TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    Assert.Equal(PptxErrorCodes.OperationUnsupported, result.Error.Code);
    Assert.Contains("reorder, remove, retime and simple entrance", result.Error.Message, StringComparison.Ordinal);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_trigger_sequence()
  {
    string path = Animated("trigger-sequence.pptx", 2);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new SetAnimation(1, 1, null, "onShapeClick", null));
    Assert.Equal(PptxErrorCodes.OperationUnsupported, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_multi_step_effect_chain()
  {
    string path = Animated("chain.pptx", 2);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new SetAnimation(1, 1, "fade then wipe", null, null));
    Assert.Equal(PptxErrorCodes.OperationUnsupported, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_added_effect_that_names_no_shape()
  {
    string path = Animated("no-shape.pptx", 1);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new SetAnimation(1, 2, "fade", null, null));
    Assert.Equal(PptxErrorCodes.InvalidAddress, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_added_effect_on_a_shape_the_slide_does_not_have()
  {
    string path = Animated("no-target.pptx", 1);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new SetAnimation(1, 2, "fade s1.sh9", null, null));
    Assert.Equal(PptxErrorCodes.AddressNotFound, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_negative_duration()
  {
    string path = Animated("negative.pptx", 1);
    byte[] before = await Bytes(path);
    string code = await EditCode(path, new SetAnimation(1, 1, null, null, -5));
    Assert.Equal(PptxErrorCodes.InvalidAddress, code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_an_effect_for_a_slide_the_deck_does_not_have()
  {
    string path = Animated("no-slide.pptx", 1);
    string code = await EditCode(path, new RemoveAnimation(4, 1));
    Assert.Equal(PptxErrorCodes.SlideNotFound, code);
  }

  [Fact]
  public async Task Reads_the_sample_decks_effects_without_error()
  {
    Assert.SkipUnless(File.Exists(SampleDeck), "the sample deck is not on this machine");
    PptxSlideSnapshot slide = await SlideRead(SampleDeck, 1);
    Assert.Empty(slide.Animations);
  }

  [Fact]
  public async Task Edits_the_sample_decks_animation_on_a_copy_and_leaves_the_original_alone()
  {
    Assert.SkipUnless(File.Exists(SampleDeck), "the sample deck is not on this machine");
    byte[] original = await Bytes(SampleDeck);
    string copy = Path.Combine(TestTempRoot.CreateDirectory("pptx-animation").FullName, "sample.pptx");
    File.Copy(SampleDeck, copy);
    try
    {
      _ = await Edit(copy, new SetAnimation(1, 1, "fade s1.sh2", null, null));
      PptxAnimationInfo effect = Assert.Single((await SlideRead(copy, 1)).Animations);
      Assert.Equal("s1.sh2", effect.Address);
      Assert.Equal("entr:10", effect.Effect);
      Assert.Equal("onClick", effect.Trigger);
      Assert.Equal(original, await Bytes(SampleDeck));
    }
    finally
    {
      File.Delete(copy);
    }
  }

  private static string Animated(string name, int effects)
    => TestPptxFactory.CreateDeck(ScratchFile(name),
        [new TestSlideSpec { Title = "T", Body = "B", AnimationEffects = effects }]);

  /// <summary>The slide part's XML, which is where the timing element lives.</summary>
  private static string SlideXml(string path, int slide)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlidePart part = doc.PresentationPart!.SlideParts.ElementAt(slide - 1);
    return part.Slide!.OuterXml;
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

  private async Task<string> EditCode(string path, params PptxOperation[] operations)
  {
    FileVersion version = (await Outline(path).ConfigureAwait(true)).Version;
    Result<PptxEditOutcome> result = await _access
        .EditAsync(path, version, operations, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    return result.Error.Code;
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

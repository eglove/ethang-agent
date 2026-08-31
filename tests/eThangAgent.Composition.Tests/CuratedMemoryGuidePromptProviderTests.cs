namespace eThangAgent.Composition.Tests;

/// <summary>The curated-memory guide must steer the model toward search-before-add
/// and toward pruning stale memories, naming the memories.prune action.</summary>
public class CuratedMemoryGuidePromptProviderTests
{
  [Fact]
  public void Guide_NamesThePruneAction_AndSearchBeforeAdd()
  {
    string guide = new CuratedMemoryGuidePromptProvider().Build();

    Assert.Contains("memories.purge", guide, StringComparison.Ordinal);
    Assert.Contains("before adding", guide, StringComparison.Ordinal);
  }
}

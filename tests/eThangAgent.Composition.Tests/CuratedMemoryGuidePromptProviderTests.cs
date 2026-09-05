namespace eThangAgent.Composition.Tests;

/// <summary>The curated-memory guide must steer the model toward search-before-add
/// and toward pruning stale memories, naming the memories.prune action.</summary>
public class CuratedMemoryGuidePromptProviderTests
{
  [Fact]
  public void Guide_SteersTowardMemorySearchOnRepeatedFailure()
  {
    // Grand plan: search memory on failures - a repeat failure or a fix that
    // doesn't stick must trigger a recall before another blind retry.
    string guide = new CuratedMemoryGuidePromptProvider().Build();

    Assert.Contains("memory.recall", guide, StringComparison.Ordinal);
    Assert.Contains("memories.search", guide, StringComparison.Ordinal);
    Assert.Contains("failure", guide, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("retry", guide, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Guide_NamesThePruneAction_AndSearchBeforeAdd()
  {
    string guide = new CuratedMemoryGuidePromptProvider().Build();

    Assert.Contains("memories.purge", guide, StringComparison.Ordinal);
    Assert.Contains("before adding", guide, StringComparison.Ordinal);
  }
}

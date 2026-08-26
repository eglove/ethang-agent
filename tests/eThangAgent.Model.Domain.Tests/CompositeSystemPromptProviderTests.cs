using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class CompositeSystemPromptProviderTests
{
  [Fact]
  public void Build_JoinsSegmentsInOrder()
  {
    CompositeSystemPromptProvider composite = new(
        [new StaticPromptProvider("first"), new StaticPromptProvider("second")]);

    Assert.Equal("first\n\nsecond", composite.Build());
  }

  [Fact]
  public void Build_SkipsNullAndWhitespaceSegments()
  {
    CompositeSystemPromptProvider composite = new(
        [new StaticPromptProvider(""), new StaticPromptProvider("   "),
             new StaticPromptProvider("real")]);

    Assert.Equal("real", composite.Build());
  }

  [Fact]
  public void Build_NoSegments_ReturnsEmpty()
  {
    CompositeSystemPromptProvider composite = new([]);

    Assert.Equal("", composite.Build());
  }
}

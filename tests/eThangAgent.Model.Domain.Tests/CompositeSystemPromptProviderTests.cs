using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class CompositeSystemPromptProviderTests
{
    [Fact]
    public void Build_JoinsSegmentsInOrder()
    {
        var composite = new CompositeSystemPromptProvider(
            [new StaticPromptProvider("first"), new StaticPromptProvider("second")]);

        Assert.Equal("first\n\nsecond", composite.Build());
    }

    [Fact]
    public void Build_SkipsNullAndWhitespaceSegments()
    {
        var composite = new CompositeSystemPromptProvider(
            [new StaticPromptProvider(""), new StaticPromptProvider("   "),
             new StaticPromptProvider("real")]);

        Assert.Equal("real", composite.Build());
    }

    [Fact]
    public void Build_NoSegments_ReturnsEmpty()
    {
        var composite = new CompositeSystemPromptProvider([]);

        Assert.Equal("", composite.Build());
    }
}

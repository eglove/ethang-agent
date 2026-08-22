namespace eThangAgent.MemoryDomain.Tests;

public class MemoryQueryPlanTests
{
    [Fact]
    public void Plan_NullQuery_ReturnsBrowse()
    {
        Assert.IsType<MemoryQueryPlan.Browse>(MemoryQueryPlan.Plan(null));
    }

    [Fact]
    public void Plan_WhitespaceQuery_ReturnsBrowse()
    {
        Assert.IsType<MemoryQueryPlan.Browse>(MemoryQueryPlan.Plan("   \t "));
    }

    [Fact]
    public void Plan_LiteralDefaultMode_ProducesTerms()
    {
        Assert.IsType<MemoryQueryPlan.Terms>(MemoryQueryPlan.Plan("hello"));
    }

    [Fact]
    public void Plan_LiteralWithRegexMetacharacters_TreatsAsTerms_NeverRegex()
    {
        var plan = Assert.IsType<MemoryQueryPlan.Terms>(MemoryQueryPlan.Plan("a.c"));
        Assert.Equal(new[] { "a", "c" }, plan.Tokens);
    }

    [Fact]
    public void Plan_Terms_AreDistinctInFirstOccurrenceOrder()
    {
        var plan = Assert.IsType<MemoryQueryPlan.Terms>(MemoryQueryPlan.Plan("dog cat dog bird cat"));
        Assert.Equal(new[] { "dog", "cat", "bird" }, plan.Tokens);
    }

    [Fact]
    public void Plan_RegexMode_PassesPatternThroughRaw()
    {
        var plan = Assert.IsType<MemoryQueryPlan.RegexPattern>(MemoryQueryPlan.Plan("a.c+^$", "regex"));
        Assert.Equal("a.c+^$", plan.Pattern);
    }

    [Fact]
    public void Plan_UnknownMode_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MemoryQueryPlan.Plan("query", "fuzzy"));
    }
}

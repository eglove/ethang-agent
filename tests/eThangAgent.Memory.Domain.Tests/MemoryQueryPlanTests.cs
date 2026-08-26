namespace eThangAgent.MemoryDomain.Tests;

public class MemoryQueryPlanTests
{
  [Fact]
  public void Plan_NullQuery_ReturnsBrowse() => _ = Assert.IsType<BrowsePlan>(MemoryQueryPlan.Plan(null));

  [Fact]
  public void Plan_WhitespaceQuery_ReturnsBrowse() => _ = Assert.IsType<BrowsePlan>(MemoryQueryPlan.Plan("   \t "));

  [Fact]
  public void Plan_LiteralDefaultMode_ProducesTerms() => _ = Assert.IsType<TermsPlan>(MemoryQueryPlan.Plan("hello"));

  [Fact]
  public void Plan_LiteralWithRegexMetacharacters_TreatsAsTerms_NeverRegex()
  {
    TermsPlan plan = Assert.IsType<TermsPlan>(MemoryQueryPlan.Plan("a.c"));
    Assert.Equal(["a", "c"], plan.Tokens);
  }

  [Fact]
  public void Plan_Terms_AreDistinctInFirstOccurrenceOrder()
  {
    TermsPlan plan = Assert.IsType<TermsPlan>(MemoryQueryPlan.Plan("dog cat dog bird cat"));
    Assert.Equal(["dog", "cat", "bird"], plan.Tokens);
  }

  [Fact]
  public void Plan_RegexMode_PassesPatternThroughRaw()
  {
    RegexPatternPlan plan = Assert.IsType<RegexPatternPlan>(MemoryQueryPlan.Plan("a.c+^$", "regex"));
    Assert.Equal("a.c+^$", plan.Pattern);
  }

  [Fact]
  public void Plan_UnknownMode_ThrowsArgumentException() => _ = Assert.Throws<ArgumentException>(() => MemoryQueryPlan.Plan("query", "fuzzy"));
}

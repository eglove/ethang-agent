using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class SmartSelectionOptionsTests
{
  [Fact]
  public void Default_IsAllUnconstrained_PriceSort()
  {
    SmartSelectionOptions options = SmartSelectionOptions.Default;
    Assert.Null(options.MaxInputPerM);
    Assert.Null(options.MaxOutputPerM);
    Assert.Null(options.MinIntelligence);
    Assert.Null(options.MinCoding);
    Assert.Null(options.MinAgentic);
    Assert.Null(options.MinContextWindow);
    Assert.Equal(SmartSortCriterion.Price, options.Sort);
  }

  [Fact]
  public void ToJson_Then_TryFromJson_RoundTrips()
  {
    SmartSelectionOptions options = new(2.5m, 10m, 40.0, 50.0, 20.0, 128_000, SmartSortCriterion.Coding);

    bool parsed = SmartSelectionOptions.TryFromJson(options.ToJson(), out SmartSelectionOptions round);

    Assert.True(parsed);
    Assert.Equal(options, round);
  }

  [Fact]
  public void TryFromJson_MissingMembers_YieldUnconstrainedPrice()
  {
    bool parsed = SmartSelectionOptions.TryFromJson("{}", out SmartSelectionOptions options);

    Assert.True(parsed);
    Assert.Equal(SmartSelectionOptions.Default, options);
  }

  [Fact]
  public void TryFromJson_UnknownSort_ReturnsFalse()
    => Assert.False(SmartSelectionOptions.TryFromJson(/*lang=json,strict*/ "{\"sort\":\"cheapest\"}", out _));
  [Fact]
  public void TryFromJson_MalformedJson_ReturnsFalse()
    => Assert.False(SmartSelectionOptions.TryFromJson("not json", out _));
  [Fact]
  public void TryFromJson_SortIsCaseInsensitive()
  {
    Assert.True(SmartSelectionOptions.TryFromJson(/*lang=json,strict*/ "{\"sort\":\"CODING\"}", out SmartSelectionOptions options));

    Assert.Equal(SmartSortCriterion.Coding, options.Sort);
  }
}

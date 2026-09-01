using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Strict binding of SubAgent configuration into SubAgentOptions at the
///     composition root. Absent SubAgent:DefaultModel is legal; present-but-empty is a
///     startup error. MaxConcurrentAgents is required; absent, non-integer, or below-1
///     values are startup errors. There is deliberately no timeout key (FR-L4/A4): the
///     tests pin that the key is GONE — an unknown key is simply not bound.</summary>
public class SubAgentConfigurationTests
{
  [Fact]
  public void Bind_MissingSection_UsesDefaults()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind(defaultModel: null, maxConcurrentAgents: "2");

    Assert.Equal(2, options.MaxConcurrentAgents);
    Assert.Null(options.DefaultModel);
    Assert.Equal(3, options.MaxDepth);
  }

  [Fact]
  public void Bind_ExplicitValues_Bind()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind("provider/model", "2");

    Assert.Equal("provider/model", options.DefaultModel);
    Assert.Equal(2, options.MaxConcurrentAgents);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Bind_EmptyModelString_IsStartupError(string model)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(model, "2"));

    Assert.Contains("SubAgent:DefaultModel", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Bind_MissingMaxConcurrentAgents_IsStartupError()
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, null));

    Assert.Equal("SubAgent:MaxConcurrentAgents is required. Set it to a positive integer.",
        error.Message);
  }

  [Fact]
  public void Bind_UnparseableMaxConcurrentAgents_IsStartupError()
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, "abc"));

    Assert.Contains("must be a positive integer of at least 1, got 'abc'.",
        error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("0")]
  [InlineData("-2")]
  public void Bind_MaxConcurrentAgentsBelowOne_IsStartupError(string maxConcurrentAgents)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, maxConcurrentAgents));

    Assert.Contains("must be a positive integer of at least 1, got '" + maxConcurrentAgents + "'.",
        error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Bind_ExplicitMaxConcurrentAgents_FlowsIntoOptions()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind("provider/model", "4");

    Assert.Equal(4, options.MaxConcurrentAgents);
  }
}

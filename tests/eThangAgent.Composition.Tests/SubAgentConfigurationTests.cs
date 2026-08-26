using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Strict binding of SubAgent configuration into SubAgentOptions at the
///     composition root. Absent SubAgent:DefaultModel is legal; present-but-empty is a
///     startup error. ChildTimeoutSeconds defaults to 300; zero/negative/unparseable
///     values are startup errors. MaxConcurrentAgents is required; absent,
///     non-integer, or below-1 values are startup errors.</summary>
public class SubAgentConfigurationTests
{
  [Fact]
  public void Bind_MissingSection_UsesDefaults()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind(defaultModel: null, childTimeoutSeconds: null,
        maxConcurrentAgents: "2");

    Assert.Equal(2, options.MaxConcurrentAgents);

    Assert.Null(options.DefaultModel);
    Assert.Equal(TimeSpan.FromSeconds(300), options.ChildTimeout);
    Assert.Equal(3, options.MaxDepth);
  }

  [Fact]
  public void Bind_MissingModel_WithExplicitTimeout_BindsBoth()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind(defaultModel: null, childTimeoutSeconds: "45",
        maxConcurrentAgents: "2");

    Assert.Null(options.DefaultModel);
    Assert.Equal(TimeSpan.FromSeconds(45), options.ChildTimeout);
  }

  [Fact]
  public void Bind_ExplicitValues_Bind()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind("provider/model", "120", "2");

    Assert.Equal("provider/model", options.DefaultModel);
    Assert.Equal(TimeSpan.FromSeconds(120), options.ChildTimeout);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Bind_EmptyModelString_IsStartupError(string model)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(model, null, "2"));

    Assert.Contains("SubAgent:DefaultModel", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("0")]
  [InlineData("-1")]
  [InlineData("-300")]
  public void Bind_NonPositiveTimeout_IsStartupError(string seconds)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, seconds, "2"));

    Assert.Contains("SubAgent:ChildTimeoutSeconds", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("abc")]
  [InlineData("3.5")]
  [InlineData("")]
  public void Bind_UnparseableTimeout_IsStartupError(string seconds)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, seconds, "2"));

    Assert.Contains("SubAgent:ChildTimeoutSeconds", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Bind_MissingMaxConcurrentAgents_IsStartupError()
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, null, null));

    Assert.Equal("SubAgent:MaxConcurrentAgents is required. Set it to a positive integer.",
        error.Message);
  }

  [Fact]
  public void Bind_UnparseableMaxConcurrentAgents_IsStartupError()
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, null, "abc"));

    Assert.Equal("SubAgent:MaxConcurrentAgents must be a positive integer, got 'abc'.",
        error.Message);
  }

  [Theory]
  [InlineData("0")]
  [InlineData("-2")]
  public void Bind_MaxConcurrentAgentsBelowOne_IsStartupError(string maxConcurrentAgents)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, null, maxConcurrentAgents));

    Assert.Equal(
        $"SubAgent:MaxConcurrentAgents must be at least 1, got '{maxConcurrentAgents}'.",
        error.Message);
  }

  [Fact]
  public void Bind_ExplicitMaxConcurrentAgents_FlowsIntoOptions()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind("provider/model", "120", "4");

    Assert.Equal(4, options.MaxConcurrentAgents);
  }
}

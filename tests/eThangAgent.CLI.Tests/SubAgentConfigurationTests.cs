namespace eThangAgent.CLI.Tests;

/// <summary>Strict binding of SubAgent configuration into SubAgentOptions at the
///     composition root. Absent SubAgent:DefaultModel is legal; present-but-empty is a
///     startup error. ChildTimeoutSeconds defaults to 300; zero/negative/unparseable
///     values are startup errors.</summary>
public class SubAgentConfigurationTests
{
    [Fact]
    public void Bind_MissingSection_UsesDefaults()
    {
        var options = SubAgentConfiguration.Bind(defaultModel: null, childTimeoutSeconds: null);

        Assert.Null(options.DefaultModel);
        Assert.Equal(TimeSpan.FromSeconds(300), options.ChildTimeout);
        Assert.Equal(3, options.MaxDepth);
    }

    [Fact]
    public void Bind_MissingModel_WithExplicitTimeout_BindsBoth()
    {
        var options = SubAgentConfiguration.Bind(defaultModel: null, childTimeoutSeconds: "45");

        Assert.Null(options.DefaultModel);
        Assert.Equal(TimeSpan.FromSeconds(45), options.ChildTimeout);
    }

    [Fact]
    public void Bind_ExplicitValues_Bind()
    {
        var options = SubAgentConfiguration.Bind("provider/model", "120");

        Assert.Equal("provider/model", options.DefaultModel);
        Assert.Equal(TimeSpan.FromSeconds(120), options.ChildTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Bind_EmptyModelString_IsStartupError(string model)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SubAgentConfiguration.Bind(model, null));

        Assert.Contains("SubAgent:DefaultModel", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-300")]
    public void Bind_NonPositiveTimeout_IsStartupError(string seconds)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SubAgentConfiguration.Bind(null, seconds));

        Assert.Contains("SubAgent:ChildTimeoutSeconds", error.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("3.5")]
    [InlineData("")]
    public void Bind_UnparseableTimeout_IsStartupError(string seconds)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SubAgentConfiguration.Bind(null, seconds));

        Assert.Contains("SubAgent:ChildTimeoutSeconds", error.Message);
    }
}

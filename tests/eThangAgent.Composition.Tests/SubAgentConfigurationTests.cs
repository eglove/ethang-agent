using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Strict binding of SubAgent configuration into SubAgentOptions at the
///     composition root. Absent SubAgent:DefaultModel is legal; present-but-empty is a
///     startup error. MaxConcurrentAgents is required; absent, non-integer, or below-1
///     values are startup errors. The SubAgent:Watchdog section (W1.2) binds the host
///     watchdog's knobs strictly; absent keys keep the defaults. There is deliberately
///     no timeout key (FR-L4/A4): the tests pin that the key is GONE — an unknown key
///     is simply not bound.</summary>
public class SubAgentConfigurationTests
{
  [Fact]
  public void Bind_MissingSection_UsesDefaults()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind(defaultModel: null, maxConcurrentAgents: "2", out _);

    Assert.Equal(2, options.MaxConcurrentAgents);
    Assert.Null(options.DefaultModel);
    Assert.Equal(3, options.MaxDepth);
  }

  [Fact]
  public void Bind_ExplicitValues_Bind()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind("provider/model", "2", out _);

    Assert.Equal("provider/model", options.DefaultModel);
    Assert.Equal(2, options.MaxConcurrentAgents);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Bind_EmptyModelString_IsStartupError(string model)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(model, "2", out _));

    Assert.Contains("SubAgent:DefaultModel", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Bind_MissingMaxConcurrentAgents_IsStartupError()
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, null, out _));

    Assert.Equal("SubAgent:MaxConcurrentAgents is required. Set it to a positive integer.",
        error.Message);
  }

  [Fact]
  public void Bind_UnparseableMaxConcurrentAgents_IsStartupError()
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, "abc", out _));

    Assert.Contains("must be a positive integer of at least 1, got 'abc'.",
        error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("0")]
  [InlineData("-2")]
  public void Bind_MaxConcurrentAgentsBelowOne_IsStartupError(string maxConcurrentAgents)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, maxConcurrentAgents, out _));

    Assert.Contains("must be a positive integer of at least 1, got '" + maxConcurrentAgents + "'.",
        error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Bind_ExplicitMaxConcurrentAgents_FlowsIntoOptions()
  {
    SubAgentOptions options = SubAgentConfiguration.Bind("provider/model", "4", out _);

    Assert.Equal(4, options.MaxConcurrentAgents);
  }

  [Fact]
  public void Bind_RemoteHostAbsent_False()
  {
    _ = SubAgentConfiguration.Bind(null, "2", out bool remote);

    Assert.False(remote);
  }

  [Theory]
  [InlineData("true", true)]
  [InlineData("TRUE", true)]
  [InlineData("false", false)]
  public void Bind_RemoteHost_Binds(string value, bool expected)
  {
    _ = SubAgentConfiguration.Bind(null, "2", out bool remote, value);

    Assert.Equal(expected, remote);
  }

  [Theory]
  [InlineData("")]
  [InlineData("yes")]
  [InlineData("1")]
  public void Bind_RemoteHostNonBoolean_IsStartupError(string value)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.Bind(null, "2", out _, value));

    Assert.Contains("SubAgent:RemoteHost must be 'true' or 'false'", error.Message, StringComparison.Ordinal);
  }

  // --- SubAgent:Watchdog:* (W1.2) — the host-watchdog bind matrix ---

  [Fact]
  public void BindWatchdog_AllKeysAbsent_ReturnsNull()
    => Assert.Null(SubAgentConfiguration.BindWatchdog(null, null, null));

  [Fact]
  public void BindWatchdog_ValidDurations_Bind()
  {
    WatchdogSettings? watchdog = SubAgentConfiguration.BindWatchdog("00:00:02", "00:15:00", null);
    Assert.NotNull(watchdog);

    WatchdogOptions options = watchdog.ToOptions();
    Assert.Equal(TimeSpan.FromSeconds(2), options.TickInterval);
    Assert.Equal(TimeSpan.FromMinutes(15), options.IdleThreshold);
    Assert.Equal(1, options.MaxWrapUpAttempts); // attempts absent: the default survives
  }

  [Fact]
  public void BindWatchdog_AttemptsOnly_BindsWithDefaultDurations()
  {
    WatchdogSettings? watchdog = SubAgentConfiguration.BindWatchdog(null, null, "3");
    Assert.NotNull(watchdog);

    WatchdogOptions options = watchdog.ToOptions();
    Assert.Equal(TimeSpan.FromSeconds(60), options.TickInterval);
    Assert.Equal(TimeSpan.FromMinutes(15), options.IdleThreshold);
    Assert.Equal(3, options.MaxWrapUpAttempts);
  }

  [Fact]
  public void BindWatchdog_AllKeysPresent_Govern()
  {
    WatchdogOptions options = SubAgentConfiguration.BindWatchdog("00:00:10", "00:01:00", "2")!.ToOptions();

    Assert.Equal(TimeSpan.FromSeconds(10), options.TickInterval);
    Assert.Equal(TimeSpan.FromMinutes(1), options.IdleThreshold);
    Assert.Equal(2, options.MaxWrapUpAttempts);
  }

  [Fact]
  public void BindWatchdog_ZeroAttempts_Binds()
  {
    // 0 is legal: the wrap-up retry is disabled, never a startup error.
    WatchdogSettings? settings = SubAgentConfiguration.BindWatchdog(null, null, "0");
    Assert.NotNull(settings);

    Assert.Equal(0, settings.ToOptions().MaxWrapUpAttempts);
  }

  [Theory]
  [InlineData("00:00:00")]
  [InlineData("-00:00:01")]
  public void BindWatchdog_NonPositiveDuration_IsStartupError(string value)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.BindWatchdog(value, null, null));

    Assert.Contains("SubAgent:Watchdog:TickInterval", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("")]
  [InlineData("abc")]
  [InlineData("00:00")]
  public void BindWatchdog_UnparseableDuration_IsStartupError(string value)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.BindWatchdog(null, value, null));

    Assert.Contains("SubAgent:Watchdog:IdleThreshold", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("5")]
  [InlineData("90")]
  public void BindWatchdog_BareIntegerDuration_IsStartupError(string value)
  {
    // TimeSpan would silently read a bare integer as DAYS — rejected, never coerced.
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.BindWatchdog(value, null, null));

    Assert.Contains("would bind as days", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("-1")]
  [InlineData("many")]
  [InlineData("")]
  public void BindWatchdog_InvalidAttempts_IsStartupError(string value)
  {
    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
        () => SubAgentConfiguration.BindWatchdog(null, null, value));

    Assert.Contains("SubAgent:Watchdog:MaxWrapUpAttempts", error.Message, StringComparison.Ordinal);
  }
}

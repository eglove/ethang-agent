using eThangAgent.Storage.ACL;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Composition.Tests;

internal sealed class FakePreferenceStore : IAppPreferenceStore
{
  private readonly Dictionary<string, string> _values = [];
  public FakePreferenceStore With(string key, string value)
  {
    _values[key] = value;
    return this;
  }
  public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
      Task.FromResult(_values.TryGetValue(key, out string? v) ? v : null);
  public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
  {
    _values[key] = value;
    return Task.FromResult(true);
  }
  public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.Remove(key));
}

public class AgentSettingsLoaderTests
{
  [Fact]
  public async Task LoadAsync_WithoutPreferences_YieldsShippedDefaults()
  {
    AgentSettings s = await AgentSettingsLoader.LoadAsync(new FakePreferenceStore());
    Assert.Equal(AgentSettingsDefaults.MaxConcurrentAgents, s.SubAgents.MaxConcurrentAgents);
    Assert.Equal(4, s.SubAgents.MaxConcurrentAgents); // pins the shipped value itself
    Assert.Null(s.SubAgents.DefaultModel);
    Assert.False(s.RemoteHost);
    Assert.Null(s.Watchdog);
    Assert.Equal("https://openrouter.ai", s.OpenRouter.BaseUrl.ToString().TrimEnd('/'));
    Assert.Equal(ZaiConfiguration.DefaultBaseUrl, s.Zai.BaseUrl.ToString().TrimEnd('/'));
  }

  [Fact]
  public async Task LoadAsync_BindsStoredSubAgentAndWatchdogValues()
  {
    FakePreferenceStore prefs = new FakePreferenceStore()
        .With(AgentPreferenceKeys.MaxConcurrentAgents, "6")
        .With(AgentPreferenceKeys.DefaultModel, "openrouter/gpt-5")
        .With(AgentPreferenceKeys.RemoteHost, "true")
        .With(AgentPreferenceKeys.WatchdogTickInterval, "00:00:02")
        .With(AgentPreferenceKeys.WatchdogIdleThreshold, "00:10:00")
        .With(AgentPreferenceKeys.WatchdogMaxWrapUpAttempts, "2")
        .With(AgentPreferenceKeys.OpenRouterBaseUrl, "http://localhost:9944")
        .With(AgentPreferenceKeys.ZaiBaseUrl, "http://localhost:9945/api");
    AgentSettings s = await AgentSettingsLoader.LoadAsync(prefs);
    Assert.Equal(6, s.SubAgents.MaxConcurrentAgents);
    Assert.Equal("openrouter/gpt-5", s.SubAgents.DefaultModel);
    Assert.True(s.RemoteHost);
    Assert.NotNull(s.Watchdog);
    Assert.Equal(TimeSpan.FromSeconds(2), s.Watchdog.TickInterval);
    Assert.Equal(TimeSpan.FromMinutes(10), s.Watchdog.IdleThreshold);
    Assert.Equal(2, s.Watchdog.MaxWrapUpAttempts);
    Assert.Equal("http://localhost:9944/", s.OpenRouter.BaseUrl.ToString());
    Assert.Equal("http://localhost:9945/api", s.Zai.BaseUrl.ToString().TrimEnd('/'));
  }

  [Theory]
  [InlineData("subagent_max_concurrent_agents", "abc", "subagent_max_concurrent_agents")]
  [InlineData("subagent_max_concurrent_agents", "0", "subagent_max_concurrent_agents")]
  [InlineData("subagent_default_model", " ", "present but empty")]
  [InlineData("subagent_remote_host", "yes", "subagent_remote_host")]
  [InlineData("subagent_watchdog_tick_interval", "5", "bare integer")]
  [InlineData("subagent_watchdog_idle_threshold", "not-a-time", "subagent_watchdog_idle_threshold")]
  [InlineData("subagent_watchdog_max_wrap_up_attempts", "-1", "subagent_watchdog_max_wrap_up_attempts")]
  [InlineData("openrouter_base_url", "not-a-url", "openrouter_base_url")]
  [InlineData("zai_base_url", "not-a-url", "zai_base_url")]
  public async Task LoadAsync_RejectsInvalidStoredValue_NamingTheKey(
      string key, string value, string expectedFragment)
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(key, value);
    InvalidOperationException invalid = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(prefs));
    Assert.Contains(expectedFragment, invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void BindBaseUrl_BlankOrAbsent_YieldsDefault_Invalid_NamesKey()
  {
    Uri fallback = AgentSettingsLoader.BindBaseUrl(null, AgentPreferenceKeys.OpenRouterBaseUrl, "https://openrouter.ai");
    Assert.Equal("https://openrouter.ai/", fallback.ToString());
    Uri stored = AgentSettingsLoader.BindBaseUrl("http://127.0.0.1:9", AgentPreferenceKeys.OpenRouterBaseUrl, "https://openrouter.ai");
    Assert.Equal("http://127.0.0.1:9/", stored.ToString());
    InvalidOperationException invalid = Assert.Throws<InvalidOperationException>(
        () => AgentSettingsLoader.BindBaseUrl("nope", AgentPreferenceKeys.ZaiBaseUrl, "https://api.z.ai/api"));
    Assert.Contains("zai_base_url", invalid.Message, StringComparison.Ordinal);
  }
}

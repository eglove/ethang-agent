using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The store-less rebind seam (ApplySettingsAsync with no preference store)
///     surfaces binder failures with the SAME preference-key names the loader
///     surfaces for identical input — one shared translation
///     (AgentSettingsLoader.WithPreferenceKeyName), so the retired SubAgent:Config-path
///     names never reach the user from either path.</summary>
public class StorelessRebindErrorNamingTests
{
  [Fact]
  public async Task ApplySettings_WithoutStore_NamesPreferenceKey_OnInvalidMaxConcurrent()
  {
    MainViewModel vm = CreateSettingsShellWithoutStore();

    InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => vm.ApplySettingsAsync(new SettingsUpdate(
            null, null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional,
            MaxConcurrentAgentsText: "0")));

    Assert.Contains(AgentPreferenceKeys.MaxConcurrentAgents, ex.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("SubAgent:MaxConcurrentAgents", ex.Message, StringComparison.Ordinal);
    Assert.Contains("must be a positive integer", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ApplySettings_WithoutStore_NamesPreferenceKey_OnInvalidWatchdogTick()
  {
    MainViewModel vm = CreateSettingsShellWithoutStore();

    InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => vm.ApplySettingsAsync(new SettingsUpdate(
            null, null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional,
            WatchdogTickText: "5")));

    Assert.Contains(AgentPreferenceKeys.WatchdogTickInterval, ex.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("SubAgent:Watchdog", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Loader_Surfaces_The_Same_Name_For_The_Same_Input()
  {
    // The other half of the no-divergence pin: identical stored input through the
    // loader path carries the same preference-key name the rebind seam surfaced.
    FakePreferenceStore preferences = new();
    preferences.Stored[AgentPreferenceKeys.MaxConcurrentAgents] = "0";

    InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(preferences));

    Assert.Contains(AgentPreferenceKeys.MaxConcurrentAgents, ex.Message, StringComparison.Ordinal);
    Assert.Contains("must be a positive integer", ex.Message, StringComparison.Ordinal);
  }

  private static MainViewModel CreateSettingsShellWithoutStore()
    => new(null,
        new MainViewModelOptions
        {
          Settings = Settings(),
          SessionFactory = new AgentSessionFactory(Settings()),
        });

  private static AgentSettings Settings() => new(
      new OpenRouterSettings(null, new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  private sealed class FakePreferenceStore : IAppPreferenceStore
  {
    public Dictionary<string, string> Stored { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      _ = Stored.Remove(key);
      return Task.FromResult(true);
    }
  }
}

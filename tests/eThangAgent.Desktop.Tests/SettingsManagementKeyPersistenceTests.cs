using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Applying a settings update persists the management key under its own
///     preference key, DPAPI-protected like the model key, and the rebuilt
///     settings carry it for future session opens.</summary>
public class SettingsManagementKeyPersistenceTests
{
  private sealed class FakePreferenceStore : IAppPreferenceStore
  {
    public List<(string Key, string Value)> Writes { get; } = [];
    public List<string> Deletions { get; } = [];
    public Dictionary<string, string> Stored { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add((key, value));
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      Deletions.Add(key);
      _ = Stored.Remove(key);
      return Task.FromResult(true);
    }
  }

  private sealed class FakeKeyProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => $"protected:{apiKey}";
    public string? Unprotect(string storedValue)
        => storedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? storedValue["protected:".Length..]
            : null;
  }

  private static MainViewModel CreateShell(IAppPreferenceStore preferences, IApiKeyProtector protector)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-v1-abc", new Uri("https://openrouter.test")),
        new AgentDomain.SubAgentOptions(null, 2));
    return new(null,
        new MainViewModelOptions
        {
          Preferences = preferences,
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings),
          ApiKeyProtector = protector,
        });
  }

  [Fact]
  public async Task ApplySettings_Persists_And_Overlays_The_Management_Key()
  {
    FakePreferenceStore preferences = new();
    FakeKeyProtector protector = new();
    MainViewModel vm = CreateShell(preferences, protector);

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        OpenRouterManagementKey: "sk-or-mng-1"));

    Assert.Contains(preferences.Writes, w => w.Key == OpenRouterSettings.ManagementKeyPreferenceKey
        && w.Value == "protected:sk-or-mng-1");
    Assert.Equal("sk-or-mng-1", vm.ConfiguredOpenRouterManagementKey);
  }

  [Fact]
  public async Task ApplySettings_Null_Clears_The_Management_Key()
  {
    FakePreferenceStore preferences = new();
    FakeKeyProtector protector = new();
    MainViewModel vm = CreateShell(preferences, protector);

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        OpenRouterManagementKey: null));

    Assert.Contains(preferences.Deletions, k => k == OpenRouterSettings.ManagementKeyPreferenceKey);
    Assert.Null(vm.ConfiguredOpenRouterManagementKey);
  }
}

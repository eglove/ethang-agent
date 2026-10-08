using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Saving a management key in Settings must push it onto every open
///     session's live carrier immediately — the frozen snapshot forced a session
///     restart to pick the key up (2026-10-08). A session without a carrier
///     (headless stub) skips the push silently.</summary>
public class ManagementKeyLivePushTests
{
  private const string ShellRoot = @"C:\work\mng-key-push";

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

  private static MainViewModel CreateShell(IAppPreferenceStore preferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider))),
          new MainViewModelOptions
          {
            Preferences = preferences,
            // ApplySettingsAsync returns early without settings — the guard the
            // production shell always satisfies.
            Settings = new AgentSettings(
                new OpenRouterSettings("sk-or-v1-abc", new Uri("https://openrouter.test")),
                new AgentDomain.SubAgentOptions(null, 2)),
          });

  private static AgentSession BuildSession(string root, string provider)
  {
    ServiceCollection services = new();
    OpenRouterManagementKeyCarrier carrier = new();
    _ = services.AddSingleton(carrier);
    return new AgentSession(
        services.BuildServiceProvider(),
        AgentDomain.AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new AgentDomain.BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }

  [Fact]
  public async Task ApplySettings_Pushes_The_Key_Into_Open_Tabs_Carriers()
  {
    FakePreferenceStore preferences = new();
    MainViewModel shell = CreateShell(preferences);
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(ShellRoot, "openrouter").ConfigureAwait(true);
    Assert.True(opened.IsSuccess);

    OpenRouterManagementKeyCarrier carrier = opened.Value.Container.Services.GetRequiredService<OpenRouterManagementKeyCarrier>();
    Assert.Null(carrier.Current);

    await shell.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        OpenRouterManagementKey: "sk-or-mng-live")).ConfigureAwait(true);

    Assert.Equal("sk-or-mng-live", carrier.Current);
  }

  [Fact]
  public async Task ApplySettings_Clearing_The_Key_Pushes_Null()
  {
    FakePreferenceStore preferences = new();
    MainViewModel shell = CreateShell(preferences);
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(ShellRoot, "openrouter").ConfigureAwait(true);
    Assert.True(opened.IsSuccess);

    OpenRouterManagementKeyCarrier carrier = opened.Value.Container.Services.GetRequiredService<OpenRouterManagementKeyCarrier>();
    carrier.Current = "sk-or-mng-old";

    await shell.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        OpenRouterManagementKey: null)).ConfigureAwait(true);

    Assert.Null(carrier.Current);
  }
}

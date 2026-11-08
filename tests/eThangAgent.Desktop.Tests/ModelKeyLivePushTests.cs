using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Saving a model key in Settings must push it onto every open session's live
///     carrier immediately: a key minted by sign-in was otherwise lost to the snapshot the
///     session was opened with, so the OLD key kept making the calls. A session without a
///     carrier (headless stub) skips the push silently.</summary>
public class ModelKeyLivePushTests
{
  private const string ShellRoot = @"C:\work\model-key-push";

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
            Settings = new AgentSettings(
                new OpenRouterSettings("sk-or-v1-old", new Uri("https://openrouter.test")),
                new AgentDomain.AgentRunOptions(null, 2)),
          });

  private static AgentSession BuildSession(string root, string provider)
  {
    ServiceCollection services = new();
    _ = services.AddSingleton(new OpenRouterApiKeyCarrier { Current = "sk-or-v1-old" });
    return new AgentSession(
        services.BuildServiceProvider(),
        AgentDomain.AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new AgentDomain.BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }

  [Fact]
  public async Task ApplySettings_Pushes_The_Model_Key_Into_Open_Tabs_Carriers()
  {
    FakePreferenceStore preferences = new();
    MainViewModel shell = CreateShell(preferences);
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(ShellRoot, "openrouter").ConfigureAwait(true);
    Assert.True(opened.IsSuccess);

    OpenRouterApiKeyCarrier carrier = opened.Value.Container.Services
        .GetRequiredService<OpenRouterApiKeyCarrier>();
    Assert.Equal("sk-or-v1-old", carrier.Current);

    await shell.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-new", CommitStyle.Conventional))
        .ConfigureAwait(true);

    Assert.Equal("sk-or-v1-new", carrier.Current);
  }

  [Fact]
  public async Task ApplySettings_Clearing_The_Model_Key_Pushes_Null()
  {
    FakePreferenceStore preferences = new();
    MainViewModel shell = CreateShell(preferences);
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(ShellRoot, "openrouter").ConfigureAwait(true);
    Assert.True(opened.IsSuccess);

    OpenRouterApiKeyCarrier carrier = opened.Value.Container.Services
        .GetRequiredService<OpenRouterApiKeyCarrier>();

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional))
        .ConfigureAwait(true);

    Assert.Null(carrier.Current);
  }
}

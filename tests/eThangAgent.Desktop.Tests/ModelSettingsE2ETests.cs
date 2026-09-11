using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end proof that Model Settings saved per workspace + provider
///     (Task 6 persistence keys) RESTORE on session open and demonstrably reach the
///     provider wire: settings are written straight into the app preference store,
///     the session opens through the shell's production open path (the same restore
///     site the model choice uses), and the mock provider's captured request body
///     carries the configured knobs — and nothing for the unset ones. The corrupt
///     case degrades to unset: a malformed provider_settings payload never fails the
///     open and never reaches the wire.</summary>
[Collection("Desktop E2E")]
public class ModelSettingsE2ETests
{
  private static string RawCompletion(string content) =>
      JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

  [Fact]
  public async Task SavedSettings_RestoreAndReachTheWire()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-settings");
    try
    {
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);
      string workspaceRoot = ws.FullName;

      // Save settings exactly the way the Model Settings window persists them
      // (Task 6 keys) — straight into the app preference store, BEFORE the open:
      // a model choice, four sampling knobs, and serialized OpenRouter request
      // settings enabling the web-search server tool.
      _ = await host.Store.SetAsync($"model_choice:openrouter:{workspaceRoot}", "mock/sub-model",
          TestContext.Current.CancellationToken);
      _ = await host.Store.SetAsync($"sampling_prefs:openrouter:{workspaceRoot}",
          /*lang=json,strict*/ """{"temperature":"1.25","maxTokens":"999","topK":"7","seed":"42"}""",
          TestContext.Current.CancellationToken);
      _ = await host.Store.SetAsync($"provider_settings:openrouter:{workspaceRoot}",
          /*lang=json,strict*/ """{"server_tools":{"openrouter:web_search":true}}""",
          TestContext.Current.CancellationToken);

      MainViewModel shell = await OpenThroughShellAsync(host, workspaceRoot);

      // One prompt, bounded: the saved settings must already be live on the
      // session's preferences, so the very first turn serves them to the wire.
      _ = host.Mock.ReturnsForModel("mock/sub-model", RawCompletion("settings in flight"));
      await shell.Tabs[0].ViewModel.RunTurnAsync("say it back");

      // The restored model choice keeps selection from running: exactly one request.
      string body = Assert.Single(host.Mock.RequestBodies);
      using JsonDocument doc = JsonDocument.Parse(body);
      JsonElement root = doc.RootElement;

      // The choice restored pre-turn and reached the wire.
      Assert.Equal("mock/sub-model", root.GetProperty("model").GetString());

      // Configured sampling knobs present under their wire keys.
      Assert.Equal(1.25f, root.GetProperty("temperature").GetSingle());
      Assert.Equal(999, root.GetProperty("max_tokens").GetInt32());
      Assert.Equal(7, root.GetProperty("top_k").GetInt32());
      Assert.Equal(42, root.GetProperty("seed").GetInt32());

      // Unset keys stay absent from the wire (never serialized as defaults).
      Assert.False(root.TryGetProperty("top_p", out _));
      Assert.False(root.TryGetProperty("top_a", out _));
      Assert.False(root.TryGetProperty("verbosity", out _));

      // The restored OpenRouter settings enable the web-search server tool,
      // which leads the tools array as its wire-typed entry.
      JsonElement tools = root.GetProperty("tools");
      Assert.Equal(JsonValueKind.Array, tools.ValueKind);
      Assert.Equal("openrouter:web_search", tools[0].GetProperty("type").GetString());
    }
    finally
    {
      ws.Delete(true);
    }
  }

  [Fact]
  public async Task MalformedProviderSettings_DegradesToUnset_AndTurnStillRuns()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-settings-bad");
    try
    {
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);
      string workspaceRoot = ws.FullName;

      // Valid knobs beside a CORRUPT provider_settings payload: the open must
      // degrade the corrupt value to unset — never throw, never fail the open.
      _ = await host.Store.SetAsync($"model_choice:openrouter:{workspaceRoot}", "mock/sub-model",
          TestContext.Current.CancellationToken);
      _ = await host.Store.SetAsync($"sampling_prefs:openrouter:{workspaceRoot}",
          /*lang=json,strict*/ """{"temperature":"1.25"}""",
          TestContext.Current.CancellationToken);
      _ = await host.Store.SetAsync($"provider_settings:openrouter:{workspaceRoot}",
          "{ this is not json !!!", TestContext.Current.CancellationToken);

      MainViewModel shell = await OpenThroughShellAsync(host, workspaceRoot);

      // The corrupt value never landed on the live preferences.
      Assert.Null(shell.Tabs[0].Container.Preferences!.ProviderSettings);

      _ = host.Mock.ReturnsForModel("mock/sub-model", RawCompletion("degraded turn ok"));
      await shell.Tabs[0].ViewModel.RunTurnAsync("turn after corrupt restore");

      // The turn reached the provider and the valid knob still rode the wire. The
      // agent's own tool surface rides every request, but the corrupt settings
      // contributed NO server-tool entry: no openrouter:* type among the tools.
      string body = Assert.Single(host.Mock.RequestBodies);
      using JsonDocument doc = JsonDocument.Parse(body);
      JsonElement root = doc.RootElement;
      Assert.Equal("mock/sub-model", root.GetProperty("model").GetString());
      Assert.Equal(1.25f, root.GetProperty("temperature").GetSingle());
      Assert.False(root.TryGetProperty("plugins", out _));
      if (root.TryGetProperty("tools", out JsonElement tools))
      {
        foreach (JsonElement entry in tools.EnumerateArray())
        {
          string? type = entry.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
          Assert.False(type is not null && type.StartsWith("openrouter:", StringComparison.Ordinal),
              $"corrupt provider_settings leaked a server tool onto the wire: {type}");
        }
      }
    }
    finally
    {
      ws.Delete(true);
    }
  }

  /// <summary>Opens the harness's session through the shell's PRODUCTION open path
  ///     with the preference store wired — the restore site every real open uses.
  ///     The session carries the container's live preferences, so the restore writes
  ///     onto the exact instance the resolvers overlay from.</summary>
  private static async Task<MainViewModel> OpenThroughShellAsync(E2E.HostHarness host, string workspaceRoot)
  {
    AgentSession session = new(
        host.Services,
        host.RootId,
        host.Services.GetRequiredService<Conversation>(),
        host.Services.GetRequiredService<SendMessageCommandHandler>(),
        host.Services.GetRequiredService<RootSessionLifecycle>(),
        host.Services.GetRequiredService<ModelConfig>(),
        workspaceRoot,
        Providers.OpenRouter,
        host.Services.GetRequiredService<IAgentInbox>(),
        host.Services.GetRequiredService<IAgentRuntime>(),
        host.Services.GetRequiredService<SessionModelPreferences>());
    AgentSessionViewModel? sessionVmRef = null;
    Task Sink(UiStreamEvent evt)
    {
      return (sessionVmRef ?? throw new InvalidOperationException("sink fired before initialization"))
          .ApplyUiStreamEventAsync(evt);
    }
    ProviderOption option = new(Providers.OpenRouter, Providers.DisplayName(Providers.OpenRouter));
    Task<Result<AgentSession>> OpenSession(string root, string provider)
    {
      _ = root;
      _ = provider;
      return Task.FromResult(Result.Success(session));
    }

    MainViewModel shell = new(
        OpenSession,
        new MainViewModelOptions
        {
          AvailableProviders = [option],
          PreferredProviderId = Providers.OpenRouter,
          UiStreamSink = Sink,
          Preferences = host.Store,
        });
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(session.WorkspaceRoot, session.ProviderName)
        .ConfigureAwait(false);
    return opened.IsSuccess ? shell : throw new InvalidOperationException(
        $"prebuilt session failed to open: [{opened.Error.Code}] {opened.Error.Message}");
  }
}

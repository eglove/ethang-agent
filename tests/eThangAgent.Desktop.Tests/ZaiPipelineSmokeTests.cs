using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// z.ai end-to-end: the same real-composition pipeline as the OpenRouter smoke test,
/// wired exclusively for the z.ai provider against a local mock serving z.ai's chat
/// path — the coding endpoint (default CodingPlan mode) and the general endpoint
/// (GeneralApi mode). Proves the full per-session provider wiring — catalog, provider,
/// shell surface, endpoint mode, capability-tool gating — without any OpenRouter
/// transport involved.
/// </summary>
[Collection("Desktop E2E")]
public class ZaiPipelineSmokeTests
{
  [Theory]
  [InlineData(ZaiEndpointMode.CodingPlan, "/coding/paas/v4/chat/completions", false)]
  [InlineData(ZaiEndpointMode.GeneralApi, "/paas/v4/chat/completions", true)]
  public async Task ZaiSession_Through_MockProvider_Renders_Streamed_Transcript(
      ZaiEndpointMode endpointMode, string chatPath, bool capabilityToolsExpected)
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-zai-e2e-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      using MockOpenRouterServer server = new(chatPath);
      server.Start();
      _ = server.Returns(/*lang=json,strict*/ """{"choices":[{"message":{"content":"hello from z.ai"}}]}""");

      AgentSettings settings = new(
          new OpenRouterSettings(null, new Uri("https://openrouter.test")),
          new ZaiSettings("zai-test-key", server.BaseUrl, endpointMode),
          new SubAgentOptions(null, 1));

      using ServiceProvider services = new ServiceCollection()
          .AddEThangAgentCore(settings, Providers.Zai,
              ModelConfig.Create("glm-5.3", null, 256, 0.2f, 1_000_000).Value!,
              new AgentHostOptions(
                  new StubClarifyChannel(),
                  new FixedWorkspaceContext("app"),
                  new UnrootedPathResolver()))
          .BuildServiceProvider();

      // Pin the session's model through the same live-preference surface the desktop
      // model picker uses — z.ai has no selector, and without the choice the resolver
      // would serve its fallback (glm-5.3-flash) instead of the scripted glm-5.3.
      services.GetRequiredService<SessionModelPreferences>().ModelId = "glm-5.3";

      SendMessageCommandHandler handler = services.GetRequiredService<SendMessageCommandHandler>();
      RootSessionLifecycle lifecycle = services.GetRequiredService<RootSessionLifecycle>();
      Conversation conversation = services.GetRequiredService<Conversation>();

      AgentSession session = new(
          services, AgentId.NewId(), conversation, handler, lifecycle,
          ModelConfig.Create("glm-5.3", null, 256, 0.2f, 1_000_000).Value!,
          WorkspaceRoot: Directory.GetCurrentDirectory(),
          ProviderName: Providers.Zai,
          ClarifyChannel: new StubClarifyChannel(),
          Inbox: services.GetRequiredService<IAgentInbox>(),
          ChildRuntime: services.GetRequiredService<IAgentRuntime>());

      // Same reasoning as the E2E harness: no live Avalonia dispatcher exists here, so
      // the production UI-thread sink would wedge the turn inside DrainUntilIdleAsync.
      // A direct-apply sink keeps events on the turn's own thread.
      AgentSessionViewModel? sessionVmRef = null;
      Func<UiStreamEvent, Task> sink = new(evt => (sessionVmRef ??
          throw new InvalidOperationException("z.ai smoke sink fired before the session view-model was initialized"))
          .ApplyUiStreamEventAsync(evt));
      MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session, sink);
      AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
      sessionVmRef = vm;

      await vm.SubmitAsync("say hi");
      await vm.WaitForTurnAsync();

      List<AssistantTextEntry> assistant = [.. vm.Transcript.Entries.OfType<AssistantTextEntry>()];
      Assert.NotEmpty(assistant);
      Assert.Equal("hello from z.ai", string.Join("", assistant.Select(a => a.Text)));
      Assert.Equal("z.ai", vm.Status.Provider);

      // The wire conversation: the mode's chat path, the GLM model id, and never an
      // OpenRouter upstream provider routing pin.
      Assert.NotEmpty(server.ChatRequestPaths);
      Assert.All(server.ChatRequestPaths, p => Assert.Equal(chatPath, p));
      Assert.NotNull(server.LastChatRequestBody);
      Assert.Contains("glm-5.3", server.LastChatRequestBody, StringComparison.Ordinal);
      using JsonDocument doc = JsonDocument.Parse(server.LastChatRequestBody);
      Assert.False(doc.RootElement.TryGetProperty("provider", out _));

      // Capability-tool gating on the wire: the capability APIs exist only on the
      // general endpoint, so CodingPlan sessions never advertise web_search.
      if (capabilityToolsExpected)
      {
        Assert.Contains("web_search", server.LastChatRequestBody, StringComparison.Ordinal);
      }
      else
      {
        Assert.DoesNotContain("web_search", server.LastChatRequestBody, StringComparison.Ordinal);
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        File.Delete(dbPath);
      }
      catch { /* best effort */ }
#pragma warning restore CA1031
    }
  }

  private sealed class StubClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>(
            new DomainError("Cancelled", "no clarify expected in this smoke test")));
  }
}

using Avalonia.Headless.XUnit;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Drives a REAL turn — real composition, real SendMessageCommandHandler, real OpenRouter
/// client — against the local mock provider, asserting the transcript renders streamed
/// assistant text end-to-end. This is the desktop counterpart of the CLI piped E2E suite.
/// </summary>
public class DesktopPipelineSmokeTests
{
  [AvaloniaFact]
  public async Task Real_Core_Through_Mock_Provider_Renders_Streamed_Transcript()
  {
    using MockOpenRouterServer server = new();
    server.Start();
    // The canned completion is split across two SSE content deltas by the mock,
    // proving the client assembled chunks and the bridge delivered them in order.
    _ = server.Returns(/*lang=json,strict*/ """{"choices":[{"message":{"content":"hello from the mock"}}]}""");

    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", server.BaseUrl),
        new ZaiSettings(null, new Uri("https://zai.test")),
        new SubAgentOptions(null, TimeSpan.FromSeconds(30), 1));

    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("mock/model", null, 256, 0.2f, 8192).Value!,
            new AgentHostOptions(
                new StubClarifyChannel(),
                new FixedWorkspaceContext("app"),
                new UnrootedPathResolver()))
        .BuildServiceProvider();

    // Pin the session's model through the same live-preference surface the desktop
    // model picker uses — selection must not run here, or it would consume the
    // mock's scripted chat responses before the turn under test.
    services.GetRequiredService<SessionModelPreferences>().ModelId = "mock/model";

    SendMessageCommandHandler handler = services.GetRequiredService<SendMessageCommandHandler>();
    RootSessionLifecycle lifecycle = services.GetRequiredService<RootSessionLifecycle>();
    Conversation conversation = services.GetRequiredService<Conversation>();

    // The smoke test drives one agent through the same shell surface production
    // uses: a MainViewModel whose single tab wraps the composed session.
    AgentSession session = new(
        services, AgentId.NewId(), conversation, handler, lifecycle,
        ModelConfig.Create("mock/model", null, 256, 0.2f, 8192).Value!,
        WorkspaceRoot: Directory.GetCurrentDirectory(),
        ProviderName: Providers.OpenRouter,
        ClarifyChannel: new StubClarifyChannel(),
        Inbox: services.GetRequiredService<IAgentInbox>(),
        ChildRuntime: services.GetRequiredService<IAgentRuntime>());
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session).ConfigureAwait(true);
    AgentSessionViewModel vm = shell.Tabs[0].ViewModel;

    await vm.SubmitAsync("say hi").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    List<AssistantTextEntry> assistant = [.. vm.Transcript.Entries.OfType<AssistantTextEntry>()];
    Assert.NotEmpty(assistant);
    Assert.Equal("hello from the mock", string.Join("", assistant.Select(a => a.Text)));
    Assert.Equal(1, vm.MessageCount);
  }

  private sealed class StubClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>(
            new DomainError("Cancelled", "no clarify expected in this smoke test")));
  }
}

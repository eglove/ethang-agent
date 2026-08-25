using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
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
    [Fact]
    public async Task Real_Core_Through_Mock_Provider_Renders_Streamed_Transcript()
    {
        using var server = new MockOpenRouterServer();
        server.Start();
        // The canned completion is split across two SSE content deltas by the mock,
        // proving the client assembled chunks and the bridge delivered them in order.
        server.Returns("""{"choices":[{"message":{"content":"hello from the mock"}}]}""");

        var settings = new AgentSettings(
            "sk-or-test",
            new Uri(server.BaseUrl),
            new SubAgentOptions(null, TimeSpan.FromSeconds(30), 1));

        using var services = new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey!,
                ModelConfig.Create("mock/model", 256, 0.2f).Value!,
                new AgentHostOptions(
                    new StubClarifyChannel(),
                    new FixedWorkspaceContext("app"),
                    new UnrootedPathResolver()))
            .BuildServiceProvider();

        var handler = services.GetRequiredService<SendMessageCommandHandler>();
        var lifecycle = services.GetRequiredService<RootSessionLifecycle>();
        var conversation = services.GetRequiredService<Conversation>();

        // The smoke test drives one agent through the same shell surface production
        // uses: a MainViewModel whose single tab wraps the composed session.
        var session = new AgentSession(
            services, AgentId.NewId(), conversation, handler, lifecycle,
            ModelConfig.Create("mock/model", 256, 0.2f).Value!,
            WorkspaceRoot: Directory.GetCurrentDirectory(),
            ClarifyChannel: new StubClarifyChannel(),
            Inbox: services.GetRequiredService<IAgentInbox>(),
            ChildRuntime: services.GetRequiredService<IAgentRuntime>());
        var shell = await MainViewModel.ForPrebuiltSessionAsync(session);
        var vm = shell.Tabs[0].ViewModel;

        await vm.SubmitAsync("say hi");
        await vm.WaitForTurnAsync();

        var assistant = vm.Transcript.Entries
            .OfType<AssistantTextEntry>()
            .ToList();
        Assert.NotEmpty(assistant);
        Assert.Equal("hello from the mock", string.Join("", assistant.Select(a => a.Text)));
        Assert.Equal(1, vm.MessageCount);
    }

    private sealed class StubClarifyChannel : IClarifyChannel
    {
        public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
            Task.FromResult(Result<string>.Failure(
                new Error("Cancelled", "no clarify expected in this smoke test")));
    }
}
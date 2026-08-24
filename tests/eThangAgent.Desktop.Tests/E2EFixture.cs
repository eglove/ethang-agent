using System.Text.Json;
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
/// Shared machinery for headless desktop E2E tests: builds the REAL composed core
/// (real OpenRouter client, real stores, real exec) against a local mock provider
/// and drives turns through MainViewModel. Replaces the piped-CLI E2E harness.
/// </summary>
public static class E2E
{
    /// <summary>The session model id wired at the composition root (mirrors DesktopHost).</summary>
    public const string SessionModel = "stealth/ox-alpha";

    /// <summary>A disposable headless agent host: mock server + services + view-model,
    /// with storage isolated to a temp database via ETHANG_AGENT_DB.</summary>
    public sealed class Host : IDisposable
    {
        private ServiceProvider? _services;

        public MockOpenRouterServer Mock { get; } = new();

        private string DatabasePath { get; set; } = "";

        public MainViewModel Vm { get; private set; } = null!;

        /// <summary>The persisted root session id — the SAME id the view-model appends under.</summary>
        public AgentId RootId { get; private set; }

        public async Task<Host> StartAsync()
        {
            Mock.Start();
            DatabasePath = Path.Combine(Path.GetTempPath(), $"ethang-e2e-{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", DatabasePath);

            var settings = new AgentSettings(
                "sk-or-test",
                new Uri(Mock.BaseUrl),
                new SubAgentOptions(null, TimeSpan.FromSeconds(30), 2),
                MaxToolIterationsConfiguration.Default);

            _services = new ServiceCollection()
                .AddEThangAgentCore(settings, settings.ApiKey!,
                    ModelConfig.Create(SessionModel, 32 * 1024, 0.7f).Value!,
                    new AgentHostOptions(
                        new NeverClarifyChannel(),
                        new FixedWorkspaceContext("app"),
                        new UnrootedPathResolver()))
                .BuildServiceProvider();

            // Root-session bootstrap via the shared composition helper — the SAME code
            // path as the desktop host, so the persisted id and the id the view-model
            // appends under can never drift apart.
            RootId = (await RootSessionBootstrapper.PersistRootAsync(
                _services.GetRequiredService<IAgentStore>())).Value!;
            var handler = _services.GetRequiredService<SendMessageCommandHandler>();
            var lifecycle = _services.GetRequiredService<RootSessionLifecycle>();
            var conversation = _services.GetRequiredService<Conversation>();

            Vm = new MainViewModel(
                (command, ct, content, reasoning, iterationEnd, toolCall, toolResult) =>
                    handler.Handle(command, ct, content, reasoning, iterationEnd, toolCall, toolResult),
                lifecycle,
                RootId,
                conversation,
                SessionModel,
                requestClose: () => { });
            return this;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
            try { if (DatabasePath.Length > 0) File.Delete(DatabasePath); } catch { /* best effort */ }
            _services?.Dispose();
            Mock.Dispose();
        }
    }

    /// <summary>Submits one user turn and waits for it to resolve, bounded so a wedged
    /// turn fails the test instead of hanging CI.</summary>
    public static async Task RunTurnAsync(this MainViewModel vm, string input)
    {
        await vm.SubmitAsync(input);
        await vm.WaitForTurnAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Serializes an exec tool-call argument carrying one C# program.</summary>
    public static string ExecProgram(string program) =>
        JsonSerializer.Serialize(new { program });

    /// <summary>Scripted assistant response performing one exec tool call.</summary>
    public static string ExecToolCall(string id, string arguments) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new { id, type = "function", function = new { name = "exec", arguments } }
                        }
                    }
                }
            }
        });

    /// <summary>Returns the decoded content of the first tool message containing the marker
    ///     across all captured chat request bodies (never raw-substring on escaped bodies).</summary>
    public static string FindToolMessageContaining(IReadOnlyList<string> bodies, string marker)
    {
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages))
                continue;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role)
                    && role.GetString() == "tool"
                    && message.TryGetProperty("content", out var content)
                    && content.GetString() is { } text
                    && text.Contains(marker, StringComparison.Ordinal))
                    return text;
            }
        }
        Assert.Fail($"no decoded tool message containing '{marker}' found in {bodies.Count} request bodies");
        return "";
    }

    /// <summary>Returns the decoded content of the LAST tool-role message in a chat request
    ///     body (never raw-substring on escaped bodies).</summary>
    public static string GetLastToolMessage(string body)
    {
        using var doc = JsonDocument.Parse(body);
        string? last = null;
        foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role)
                && role.GetString() == "tool"
                && message.TryGetProperty("content", out var content))
                last = content.GetString();
        }
        Assert.NotNull(last);
        return last!;
    }

    private sealed class NeverClarifyChannel : IClarifyChannel
    {
        public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
            Task.FromResult(Result<string>.Failure(
                new Error("Cancelled", "no clarify expected in this E2E scenario")));
    }
}

/// <summary>All desktop E2E classes share one xUnit collection: they mutate the process-wide
///     ETHANG_AGENT_DB variable, so parallel classes must not race it.</summary>
[CollectionDefinition("Desktop E2E")]
public sealed class DesktopE2ECollection { }
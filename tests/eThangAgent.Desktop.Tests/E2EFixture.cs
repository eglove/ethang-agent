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

        /// <summary>The single agent tab's view-model — the chat surface under test.</summary>
        public AgentSessionViewModel Vm { get; private set; } = null!;

        /// <summary>The shell hosting the agent tab (production window shape).</summary>
        public MainViewModel Shell { get; private set; } = null!;

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
                new SubAgentOptions(null, TimeSpan.FromSeconds(30), 2));

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

            // The E2E host drives one agent through the same shell surface production
            // uses: a MainViewModel whose single tab wraps the composed session.
            var session = new AgentSession(
                _services!, RootId, conversation, handler, lifecycle,
                ModelConfig.Create(SessionModel, 32 * 1024, 0.7f).Value!,
                WorkspaceRoot: Directory.GetCurrentDirectory(),
                ClarifyChannel: new NeverClarifyChannel(),
                Inbox: _services!.GetRequiredService<IAgentInbox>(),
                ChildRuntime: _services!.GetRequiredService<IAgentRuntime>());
            // No live Avalonia session exists in headless tests, so the production sink
            // (ApplyUiStreamEventOnUIThreadAsync) posts onto Dispatcher.UIThread, where queued
            // operations never execute (shut-down unit-test dispatcher) — wedging every turn
            // inside DrainUntilIdleAsync. Inject a shell-level sink that applies events directly,
            // mirroring TestFixtures.CreateViewModel(marshalToUIThread: false).
            AgentSessionViewModel? sessionVmRef = null;
            var sink = new Func<UiStreamEvent, Task>(evt => (sessionVmRef ??
                throw new InvalidOperationException("E2E stream sink fired before the session view-model was initialized"))
                .ApplyUiStreamEventAsync(evt));
            Shell = await MainViewModel.ForPrebuiltSessionAsync(session, sink);
            Vm = Shell.Tabs[0].ViewModel;
            sessionVmRef = Vm;
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
    public static async Task RunTurnAsync(this AgentSessionViewModel vm, string input)
    {
        // SubmitAsync RETURNS the running turn task, so awaiting it covers the whole turn
        // including DrainUntilIdleAsync — it must carry its own bound, otherwise a wedged
        // drain hangs before the WaitForTurnAsync timeout below is ever reached.
        await vm.SubmitAsync(input).WaitAsync(TimeSpan.FromSeconds(60));
        await vm.WaitForTurnAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Serializes an exec tool-call argument carrying one C# program and the
    ///     mandatory per-call execution budget.</summary>
    public static string ExecProgram(string program) =>
        JsonSerializer.Serialize(new { timeoutSeconds = 120, program });

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
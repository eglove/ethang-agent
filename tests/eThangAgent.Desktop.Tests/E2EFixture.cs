using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Shared machinery for headless desktop E2E tests: builds the REAL composed core
/// (real OpenRouter client, real stores, real exec) against a local mock provider
/// and drives turns through MainViewModel. Replaces the piped-CLI E2E harness.
/// </summary>
internal static class E2E
{
  /// <summary>The session model id wired at the composition root (mirrors DesktopHost).</summary>
  public const string SessionModel = "openrouter/auto";

  /// <summary>A disposable headless agent host: mock server + services + view-model,
  /// with storage isolated to a temp database via ETHANG_AGENT_DB.</summary>
  internal sealed class HostHarness : IDisposable
  {
    private ServiceProvider? _services;

    public MockOpenRouterServer Mock { get; } = new();

    private string DatabasePath { get; set; } = "";

    /// <summary>The single agent tab's view-model — the chat surface under test.</summary>
    internal AgentSessionViewModel Vm { get; private set; } = null!;

    /// <summary>The shell hosting the agent tab (production window shape).</summary>
    internal MainViewModel Shell { get; private set; } = null!;

    /// <summary>The persisted root session id — the SAME id the view-model appends under.</summary>
    public AgentId RootId { get; private set; }

    public async Task<HostHarness> StartAsync()
    {
      Mock.Start();
      // Catalog the two mock models so the session's window source resolves them;
      // a spawn of a model with no window fails by design (strict correctness).
      _ = Mock.ReturnsCatalog(/*lang=json,strict*/ """{ "data": [ { "id": "mock/sub-model", "pricing": { "prompt": "0.000001", "completion": "0.000002" }, "context_length": 32768, "top_provider": { "max_completion_tokens": 8192 }, "architecture": { "modality": "text->text" } } ] }""");
      DatabasePath = Path.Combine(Path.GetTempPath(), $"ethang-e2e-{Guid.NewGuid():N}.db");
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", DatabasePath);

      AgentSettings settings = BuildSettings();

      _services = new ServiceCollection()
          .AddEThangAgentCore(settings, Providers.OpenRouter,
              ModelConfig.Create(SessionModel, null, 32 * 1024, 0.7f, 32 * 1024).Value!,
              new AgentHostOptions(
                  new NeverClarifyChannel(),
                  new FixedWorkspaceContext("app"),
                  new UnrootedPathResolver()))
          .BuildServiceProvider();

      // Pin the session's model through the same live-preference surface the desktop
      // model picker uses — selection must not run here, or it would consume the
      // mock's scripted chat responses before the turn under test.
      _services.GetRequiredService<SessionModelPreferences>().ModelId = SessionModel;

      // Root-session bootstrap via the shared composition helper — the SAME code
      // path as the desktop host, so the persisted id and the id the view-model
      // appends under can never drift apart. The binding (workspace + provider) is
      // what resume needs to rehydrate this session later.
      string workspaceRoot = Directory.GetCurrentDirectory();
      RootId = (await RootSessionBootstrapper.PersistRootAsync(
          _services.GetRequiredService<IAgentStore>(), workspaceRoot,
          Providers.OpenRouter).ConfigureAwait(false)).Value;
      SendMessageCommandHandler handler = _services.GetRequiredService<SendMessageCommandHandler>();
      RootSessionLifecycle lifecycle = _services.GetRequiredService<RootSessionLifecycle>();
      Conversation conversation = _services.GetRequiredService<Conversation>();

      // The E2E host drives one agent through the same shell surface production
      // uses: a MainViewModel whose single tab wraps the composed session.
      AgentSession session = new(
          _services, RootId, conversation, handler, lifecycle,
          ModelConfig.Create(SessionModel, null, 32 * 1024, 0.7f, 32 * 1024).Value!,
          WorkspaceRoot: workspaceRoot,
          ProviderName: Providers.OpenRouter,
          ClarifyChannel: new NeverClarifyChannel(),
          Inbox: _services.GetRequiredService<IAgentInbox>(),
          ChildRuntime: _services.GetRequiredService<IAgentRuntime>());
      // No live Avalonia session exists in headless tests, so the production sink
      // (ApplyUiStreamEventOnUIThreadAsync) posts onto Dispatcher.UIThread, where queued
      // operations never execute (shut-down unit-test dispatcher) — wedging every turn
      // inside DrainUntilIdleAsync. Inject a shell-level sink that applies events directly,
      // mirroring TestFixtures.CreateViewModel(marshalToUIThread: false).
      AgentSessionViewModel? sessionVmRef = null;
      Func<UiStreamEvent, Task> sink = new(evt => (sessionVmRef ??
          throw new InvalidOperationException("E2E stream sink fired before the session view-model was initialized"))
          .ApplyUiStreamEventAsync(evt));
      Shell = await MainViewModel.ForPrebuiltSessionAsync(session, sink).ConfigureAwait(false);
      Vm = Shell.Tabs[0].ViewModel;
      sessionVmRef = Vm;
      return this;
    }

    /// <summary>The settings snapshot serving the harness: the mock server as the
    ///     OpenRouter endpoint. Shared by the live container and any resume factory.</summary>
    internal AgentSettings BuildSettings() => new(
        new OpenRouterSettings("sk-or-test", Mock.BaseUrl),
        new ZaiSettings(null, new Uri("https://zai.test")),
        new SubAgentOptions(null, 2));

    /// <summary>A factory over the SAME temp database and mock server the harness runs
    ///     on — lets tests drive the real <see cref="AgentSessionFactory.ResumeAsync"/>
    ///     path against a session this harness persisted.</summary>
    internal AgentSessionFactory CreateResumeFactory() =>
        new(BuildSettings(), new AppDatabase(DatabasePath));

    public void Dispose()
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        if (DatabasePath.Length > 0)
        {
          File.Delete(DatabasePath);
        }
      }
      catch { /* best effort */ }
#pragma warning restore CA1031
      _services?.Dispose();
      Mock.Dispose();
    }
  }

  /// <summary>Submits one user turn and waits for it to resolve, bounded so a wedged
  /// turn fails the test instead of hanging CI.</summary>
  internal static async Task RunTurnAsync(this AgentSessionViewModel vm, string input)
  {
    // SubmitAsync RETURNS the running turn task, so awaiting it covers the whole turn
    // including DrainUntilIdleAsync — it must carry its own bound, otherwise a wedged
    // drain hangs before the WaitForTurnAsync timeout below is ever reached.
    await vm.SubmitAsync(input).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    await vm.WaitForTurnAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
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
    ArgumentNullException.ThrowIfNull(bodies);
    ArgumentNullException.ThrowIfNull(marker);
    foreach (string body in bodies)
    {
      using JsonDocument doc = JsonDocument.Parse(body);
      if (!doc.RootElement.TryGetProperty("messages", out JsonElement messages))
      {
        continue;
      }

      foreach (JsonElement message in messages.EnumerateArray())
      {
        if (message.TryGetProperty("role", out JsonElement role)
            && role.GetString() == "tool"
            && message.TryGetProperty("content", out JsonElement content)
            && content.GetString() is { } text
            && text.Contains(marker, StringComparison.Ordinal))
        {
          return text;
        }
      }
    }
    Assert.Fail($"no decoded tool message containing '{marker}' found in {bodies.Count} request bodies");
    return "";
  }


  /// <summary>Returns the decoded content of the LAST tool-role message in a chat request
  ///     body (never raw-substring on escaped bodies).</summary>
  public static string GetLastToolMessage(string body)
  {
    using JsonDocument doc = JsonDocument.Parse(body);
    string? last = null;
    foreach (JsonElement message in doc.RootElement.GetProperty("messages").EnumerateArray())
    {
      if (message.TryGetProperty("role", out JsonElement role)
          && role.GetString() == "tool"
          && message.TryGetProperty("content", out JsonElement content))
      {
        last = content.GetString();
      }
    }
    Assert.NotNull(last);
    return last;
  }

  private sealed class NeverClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>(
            new DomainError("Cancelled", "no clarify expected in this E2E scenario")));
  }
}

/// <summary>All desktop E2E classes share one xUnit collection: they mutate the process-wide
///     ETHANG_AGENT_DB variable, so parallel classes must not race it.</summary>
// Named decision (CA1515): xUnit requires the collection definition type to be public
// for discovery; internal would silently split the collection into per-class runs.
#pragma warning disable CA1515 // Types can be made internal
[CollectionDefinition("Desktop E2E")]
public sealed class DesktopE2ECollections { }
#pragma warning restore CA1515

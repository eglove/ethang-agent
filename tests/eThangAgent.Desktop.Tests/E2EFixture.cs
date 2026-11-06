using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
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

    /// <summary>The claim this harness holds over the hosts its sessions spawn, released on
    ///     disposal.</summary>
    private IDisposable? _hostOwner;

    public MockOpenRouterServer Mock { get; } = new();

    internal string DatabasePath { get; private set; } = "";

    /// <summary>The single agent tab's view-model — the chat surface under test.</summary>
    internal AgentSessionViewModel Vm { get; private set; } = null!;

    /// <summary>The shell hosting the agent tab (production window shape).</summary>
    internal MainViewModel Shell { get; private set; } = null!;

    /// <summary>The persisted root session id — the SAME id the view-model appends under.</summary>
    public AgentId RootId { get; private set; }

    /// <summary>The session preference store over the SAME temp database the composed
    ///     container uses — how tests persist per-workspace preferences (model choice,
    ///     sampling settings) the production open path restores.</summary>
    public IAppPreferenceStore Store { get; private set; } = null!;

    /// <summary>Test seam: the factory producing the computer-use access provider. Set it
    ///     before StartAsync; default throws so missing wiring surfaces loudly.</summary>
    public Func<IComputerAccessProvider> ComputerAccessProviderFactory { get; set; } =
        () => throw new InvalidOperationException("no computer access provider registered for this harness");

    /// <summary>The composed session container — lets a settings E2E build a second
    ///     AgentSession over the SAME container (shared live preferences) and drive it
    ///     through a preference-store-wired shell open path.</summary>
    internal ServiceProvider Services => _services
        ?? throw new InvalidOperationException("harness not started");

    /// <param name="reuseDatabasePath">When set, the harness runs over THIS database file
    ///     instead of a fresh temp one — the restart-survival E2E opens two sessions over it.</param>
    /// <param name="workspaceRoot">When set, the harness binds THIS absolute workspace root:
    /// <param name="computerUse">When true, the harness enables computer use in the composed settings.</param>
    ///     FixedWorkspaceContext, AgentSession.WorkspaceRoot, and the root-session bootstrap all
    ///     carry it, so anchored-spawn E2E can validate anchors against a real directory. When
    ///     null, behavior is unchanged: FixedWorkspaceContext("app") and WorkspaceRoot = cwd.</param>
    /// <param name="workspaceId">When set, the harness binds THIS workspace identity instead of the
    ///     root path — the language-server daemon pipe is keyed on it (issue #361).</param>
    /// <param name="lspDaemonExePath">When set, overrides where the language-server daemon
    ///     executable is looked for, so a test can point it at a path that does not exist and
    ///     pin the LSP-absent behavior.</param>
    public async Task<HostHarness> StartAsync(string? reuseDatabasePath = null, string? workspaceRoot = null, bool computerUse = false, string? workspaceId = null, string? lspDaemonExePath = null)
    {
      Mock.Start();
      // Catalog the two mock models so the session's window source resolves them;
      // a spawn of a model with no window fails by design (strict correctness).
      // The openrouter/auto row mirrors the LIVE API (context 2,000,000; -1 pricing;
      // image modality; empty top_provider) so the routing pseudo-model resolves its
      // window from the catalog like every other model.
      _ = Mock.ReturnsCatalog(/*lang=json,strict*/ """{ "data": [ { "id": "mock/sub-model", "pricing": { "prompt": "0.000001", "completion": "0.000002" }, "context_length": 32768, "top_provider": { "max_completion_tokens": 8192 }, "architecture": { "modality": "text->text" } }, { "id": "openrouter/auto", "pricing": { "prompt": "-1", "completion": "-1" }, "context_length": 2000000, "supported_parameters": ["tools"], "architecture": { "modality": "text+image->text", "input_modalities": ["text", "image"] }, "top_provider": { "context_length": null, "max_completion_tokens": null } } ] }""");
      DatabasePath = reuseDatabasePath ?? Path.Combine(Path.GetTempPath(), $"ethang-e2e-{Guid.NewGuid():N}.db");
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", DatabasePath);

      AgentSettings settings = BuildSettings(computerUse: computerUse);

      // Byte-identical null branch (named by the brief): the harness historically wired
      // FixedWorkspaceContext("app") while the session binding carried cwd. An EXPLICIT
      // workspaceRoot (anchored-spawn E2E — spawn anchor validation requires an absolute,
      // existing root) replaces all three: the context, the binding, and the bootstrap.
      string workspaceContextId = workspaceId ?? workspaceRoot ?? "app";
      string effectiveWorkspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
      // The identity and the directory are separate facts (#317): an override supplies the
      // identity while the root stays the directory the session works in.
      FixedWorkspaceContext workspaceContext = workspaceId is null
          ? FixedWorkspaceContext.ForPath(workspaceContextId)
          : new FixedWorkspaceContext(workspaceContextId, effectiveWorkspaceRoot);

      _services = new ServiceCollection()
          .AddEThangAgentCore(settings, Providers.OpenRouter,
              ModelConfig.Create(SessionModel, 32 * 1024, 0.7f, 32 * 1024).Value!,
              new AgentHostOptions(
                  workspaceContext,
                  new UnrootedPathResolver()))
          // Computer-use E2E: tests register their fake access provider here before the
          // container seals (the composition resolves the seam lazily at tool-binding time).
          .AddSingleton(sp => ComputerAccessProviderFactory())
          // The harness drives the composition WITHOUT a ChildHost: the in-process
          // runtime is the explicit opt-in, never a production path.
          .AddInProcessAgentRuntime(settings)
          .BuildServiceProvider();

      Store = _services.GetRequiredService<IAppPreferenceStore>();
      if (lspDaemonExePath is not null)
      {
        _services.GetRequiredService<LspDaemonExePathOverride>().Path = lspDaemonExePath;
      }

      // Pin the session's model through the same live-preference surface the desktop
      // model picker uses — selection must not run here, or it would consume the
      // mock's scripted chat responses before the turn under test.
      _services.GetRequiredService<SessionModelPreferences>().ModelId = SessionModel;

      // Root-session bootstrap via the shared composition helper — the SAME code
      // path as the desktop host, so the persisted id and the id the view-model
      // appends under can never drift apart. The binding (workspace + provider) is
      // what resume needs to rehydrate this session later.
      RootId = (await RootSessionBootstrapper.PersistRootAsync(
          _services.GetRequiredService<IAgentStore>(), effectiveWorkspaceRoot,
          Providers.OpenRouter).ConfigureAwait(false)).Value;
      // Publish the persisted root id into the container, exactly as AgentSessionFactory
      // does after PersistRootAsync — without this the session-stamp consumers (plan show
      // envelope, ModelUsed persistence) see a null id, not production behavior.
      _services.GetRequiredService<RootSessionIdentity>().Id = RootId;
      SendMessageCommandHandler handler = _services.GetRequiredService<SendMessageCommandHandler>();
      RootSessionLifecycle lifecycle = _services.GetRequiredService<RootSessionLifecycle>();
      Conversation conversation = _services.GetRequiredService<Conversation>();

      // The E2E host drives one agent through the same shell surface production
      // uses: a MainViewModel whose single tab wraps the composed session.
      AgentSession session = new(
          _services, RootId, conversation, handler, lifecycle,
          ModelConfig.Create(SessionModel, 32 * 1024, 0.7f, 32 * 1024).Value!,
          WorkspaceRoot: effectiveWorkspaceRoot,
          ProviderName: Providers.OpenRouter,
          Inbox: _services.GetRequiredService<IAgentInbox>(),
          ChildRuntime: _services.GetRequiredService<IAgentRuntime>(),
          Preferences: _services.GetRequiredService<SessionModelPreferences>());
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
    ///     OpenRouter endpoint. Shared by the live container and any resume factory.
    ///     The harness composes the core container directly, so it opts into the
    ///     in-process runtime explicitly — production never does (every conversation
    ///     runs in its own host process).</summary>
    internal AgentSettings BuildSettings(bool computerUse = false) => new(
        new OpenRouterSettings("sk-or-test", Mock.BaseUrl),
        new AgentRunOptions(null, 2),
        ComputerUse: computerUse);

    /// <summary>A factory over the SAME temp database and mock server the harness runs
    ///     on — lets tests drive the real <see cref="AgentSessionFactory.ResumeAsync"/>
    ///     path against a session this harness persisted.</summary>
    internal AgentSessionFactory CreateResumeFactory() =>
        new(BuildSettings(), new AppDatabase(DatabasePath));

    /// <summary>Claims every host this harness's sessions spawn, so a leak the suite guard
    ///     reports names the test that opened the session instead of only a pid.
    ///     <para>THE HARNESS IS THE OWNER because it is the only thing in scope when a session is
    ///     opened: the test's own name is stated here once, and every host spawned under it - the
    ///     creator's, each step's, each replacement's - carries it onto its ledger fact.</para></summary>
    public void ClaimHostsForCurrentTest() =>
        _hostOwner = HostProcessLedger.OwnedBy(TestContext.Current.Test?.TestDisplayName
            ?? GetType().Name);

    public void Dispose()
    {
      _hostOwner?.Dispose();
      _hostOwner = null;
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
    await vm.SubmitAsync(input).WaitAsync(TestCeiling.Value).ConfigureAwait(false);
    await vm.WaitForTurnAsync().WaitAsync(TestCeiling.Value).ConfigureAwait(false);
  }


  /// <summary>A minimal Responses-API request body naming one model — how the mock
  ///     concurrency test addresses the per-model scripts directly.</summary>
  public static string RawCompletionBody(string model) =>
      JsonSerializer.Serialize(new { model, stream = false, input = Array.Empty<object>() });

  /// <summary>Scripted assistant text reply in the Responses-API body shape.</summary>
  public static string RawCompletion(string content) =>
      JsonSerializer.Serialize(new
      {
        output = new object[]
        {
          new
          {
            type = "message",
            role = "assistant",
            content = new object[] { new { type = "output_text", text = content } },
          },
        },
        status = "completed",
      });

  /// <summary>Scripted assistant response performing one tool call with the given tool name
  ///     (a Responses-API function_call output item).</summary>
  public static string ToolCall(string id, string toolName, string arguments) =>
      JsonSerializer.Serialize(new
      {
        output = new object[]
        {
          new { type = "function_call", call_id = id, name = toolName, arguments },
        },
        status = "completed",
      });

  /// <summary>Serializes an exec tool-call argument carrying one C# program and the
  ///     mandatory per-call execution budget.</summary>
  public static string ExecProgram(string program) =>
      JsonSerializer.Serialize(new { timeoutSeconds = 120, title = "e2e", program });

  /// <summary>Scripted assistant response performing one exec tool call (a Responses-API
  ///     function_call output item).</summary>
  public static string ExecToolCall(string id, string arguments) =>
      JsonSerializer.Serialize(new
      {
        output = new object[]
        {
          new { type = "function_call", call_id = id, name = "exec", arguments },
        },
        status = "completed",
      });

  /// <summary>Returns the decoded content of the first tool result (function_call_output
  ///     input item) containing the marker across all captured request bodies (never
  ///     raw-substring on escaped bodies).</summary>
  public static string FindToolMessageContaining(IReadOnlyList<string> bodies, string marker)
  {
    ArgumentNullException.ThrowIfNull(bodies);
    ArgumentNullException.ThrowIfNull(marker);
    foreach (string body in bodies)
    {
      using JsonDocument doc = JsonDocument.Parse(body);
      if (!doc.RootElement.TryGetProperty("input", out JsonElement input))
      {
        continue;
      }

      foreach (JsonElement item in input.EnumerateArray())
      {
        if (TryGetToolOutputText(item, out string? text)
            && text.Contains(marker, StringComparison.Ordinal))
        {
          return text;
        }
      }
    }
    Assert.Fail($"no decoded tool result containing '{marker}' found in {bodies.Count} request bodies");
    return "";
  }


  /// <summary>Returns the decoded content of the LAST tool result (function_call_output
  ///     input item) in a request body (never raw-substring on escaped bodies).</summary>
  public static string GetLastToolMessage(string body)
  {
    using JsonDocument doc = JsonDocument.Parse(body);
    string? last = null;
    foreach (JsonElement item in doc.RootElement.GetProperty("input").EnumerateArray())
    {
      if (TryGetToolOutputText(item, out string? text))
      {
        last = text;
      }
    }
    Assert.NotNull(last);
    return last;
  }

  /// <summary>Strips the exec change-report annotation (issue #283) from a tool result,
  ///     leaving the program's own output. The annotation is harness METADATA appended to
  ///     every exec result, so a test asserting on the program's output must not see it -
  ///     exactly as it must not see the pre-existing workspace note. Tests that are ABOUT
  ///     the annotation assert on it directly instead of using this.</summary>
  public static string WithoutExecAnnotations(string toolResult)
  {
    ArgumentNullException.ThrowIfNull(toolResult);
    string[] kept = [.. toolResult
        .Split('\n')
        .Where(line => !line.StartsWith("[exec: ", StringComparison.Ordinal))];
    return string.Join("\n", kept);
  }

  /// <summary>Decodes one function_call_output item's output text: a flat string output,
  ///     or the concatenated input_text parts when the result rode an input-part array.</summary>
  private static bool TryGetToolOutputText(JsonElement item, [NotNullWhen(true)] out string? text)
  {
    text = null;
    if (item.ValueKind != JsonValueKind.Object
        || !item.TryGetProperty("type", out JsonElement type)
        || type.ValueKind != JsonValueKind.String
        || type.GetString() != "function_call_output"
        || !item.TryGetProperty("output", out JsonElement output))
    {
      return false;
    }

    if (output.ValueKind == JsonValueKind.String)
    {
      text = output.GetString();
      return text is not null;
    }

    if (output.ValueKind == JsonValueKind.Array)
    {
      System.Text.StringBuilder joined = new();
      foreach (JsonElement part in output.EnumerateArray())
      {
        if (part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("type", out JsonElement partType)
            && partType.ValueKind == JsonValueKind.String
            && partType.GetString() == "input_text"
            && part.TryGetProperty("text", out JsonElement partText)
            && partText.ValueKind == JsonValueKind.String)
        {
          _ = joined.Append(partText.GetString());
        }
      }

      text = joined.ToString();
      return text.Length > 0;
    }

    return false;
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

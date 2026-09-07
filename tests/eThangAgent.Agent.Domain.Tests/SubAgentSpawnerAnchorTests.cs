using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>T7 spawner anchoring: an anchored contract re-roots the child's tool registry
///     at the anchor and lifts the workspace anchor scope around the run (restored after);
///     an unanchored contract passes the registry through untouched and never writes the
///     scope; an anchored contract with no scope wired is an infrastructure
///     misconfiguration whose ArgumentNullException maps to Failed(ProviderError)
///     through the run's fault boundary. When a contract carries both an anchor and a
///     resolved effective tool set, the wraps compose Filtered(Anchored(session)) —
///     grants OUTSIDE the anchor: a granted call resolves re-rooted at the anchor and
///     a denied call renders the structured GrantViolation contract (the loop resolves
///     refusals through the concrete FilteredToolRegistry, so the filter must be the
///     registry the Agent carries).</summary>
public class SubAgentSpawnerAnchorTests
{
  private const string Anchor = @"C:\anchor\ws";
  private const string ParentRoot = @"C:\parent\ws";

  private static AgentRecord Child(string? contractJson, string prompt = "do things",
      string? effectiveTools = null)
      => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", null, prompt,
          new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
          contractJson is null ? null : new SpawnContract(WorkspaceRoot: contractJson,
              EffectiveTools: effectiveTools));

  private static SubAgentSpawner MakeRunner(
      IModelProvider provider, FakeAgentStore store, IToolRegistry tools,
      IWorkspaceAnchorScope? scope)
  {
    SubAgentServices services = new(
        new FakeModelProviderFactory(provider), store, tools,
        new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m/sub"),
        AnchorScope: scope);
    return new SubAgentSpawner(services);
  }

  /// <summary>One scripted tool call against the scoped tool, then the final report:
  ///     dispatch exercises the registry the Agent carries, so the run observes the
  ///     anchoring through the scoped fake tool's resolution.</summary>
  private static Result<ModelResponse>[] DispatchingProvider()
      => [
          Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "scoped", "{}")])),
          Result.Success(new ModelResponse("done", [])),
      ];

  // ── (a) anchored contract: re-rooted dispatch + scope observed during the run ──

  [Fact]
  public async Task RunAsync_AnchoredContract_RootsDispatchAtAnchor_AndSetsScopeDuringRun()
  {
    FakeAgentStore store = new();
    RecordingScope scope = new();
    ExecLog log = new();
    ScopedFakeTool scoped = new("scoped", log);
    RecordingProvider provider = new(scope, DispatchingProvider());
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([scoped]), scope);

    AgentRunOutcome outcome = await spawner.RunAsync(
        Child(Anchor), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);

    // The registry the Agent carried re-rooted the scoped tool at the contract's
    // anchor: the sentinel (not the original) served the dispatch.
    Assert.Equal(Anchor, scoped.RootedAtArg);
    Assert.Equal([SentinelTool.SentinelExec], log.Entries);

    // The scope held the anchor at every provider call of the run…
    Assert.Equal(new string?[] { Anchor, Anchor }, provider.ScopeSamples);

    // …and was restored (to null) after.
    Assert.Null(scope.Current);
  }

  // ── (b) unanchored contract: registry untouched, scope never written ────────

  [Fact]
  public async Task RunAsync_UnanchoredContract_DispatchesUnrooted_ScopeNeverWritten()
  {
    FakeAgentStore store = new();
    RecordingScope scope = new();
    ExecLog log = new();
    ScopedFakeTool scoped = new("scoped", log);
    RecordingProvider provider = new(scope, DispatchingProvider());
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([scoped]), scope);

    AgentRunOutcome outcome = await spawner.RunAsync(
        Child(null), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);

    // The shared registry itself served the run: no re-rooting ever happened.
    Assert.Null(scoped.RootedAtArg);
    Assert.Equal([ScopedFakeTool.OriginalExec], log.Entries);

    // The scope was never written during the run and stays null after.
    Assert.Equal(new string?[] { null, null }, provider.ScopeSamples);
    Assert.Null(scope.Current);
  }

  // ── (c) anchored contract, no scope wired: wiring fault → ProviderError ─────

  [Fact]
  public async Task RunAsync_AnchoredContract_WithoutScope_TerminatesProviderError()
  {
    FakeAgentStore store = new();
    ExecLog log = new();
    ScopedFakeTool scoped = new("scoped", log);
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([scoped]), scope: null);

    // An anchored contract with no scope wired is an infrastructure misconfiguration:
    // the ArgumentNullException at the check site (documented as strictly a wiring
    // fault) maps through the run's catch(Exception) boundary to a well-formed
    // Failed(ProviderError) outcome — never a crash of the spawning agent.
    AgentRunOutcome outcome = await spawner.RunAsync(
        Child(Anchor), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.ProviderError, outcome.Reason);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
    Assert.Empty(log.Entries); // no dispatch happened: the run failed before the loop
  }

  // ── (d) scope restore: previous non-null value survives the run ─────────────

  [Fact]
  public async Task RunAsync_AnchoredContract_RestoresPreviousScopeValue()
  {
    FakeAgentStore store = new();
    RecordingScope scope = new() { Current = ParentRoot };
    ExecLog log = new();
    ScopedFakeTool scoped = new("scoped", log);
    RecordingProvider provider = new(scope,
        Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([scoped]), scope);

    AgentRunOutcome outcome = await spawner.RunAsync(
        Child(Anchor), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);

    // The run observed the CHILD's anchor (chained grandchild scenario), and the
    // previous value was restored exactly afterwards.
    Assert.Equal([Anchor], provider.ScopeSamples);
    Assert.Equal(ParentRoot, scope.Current);
  }

  // ── (f) chain order: anchored + granted → Filtered(Anchored(session)) ───────

  [Fact]
  public async Task RunAsync_AnchoredAndGrantedContract_ComposesFilteredOutsideAnchored()
  {
    FakeAgentStore store = new();
    RecordingScope scope = new();
    ExecLog log = new();
    ScopedFakeTool scoped = new("scoped", log);
    SentinelTool ungranted = new("ungranted", log);
    // Grants admit ONLY "scoped". The denied-but-real "ungranted" probe distinguishes
    // the topologies: the loop resolves refusal explanations through the concrete
    // FilteredToolRegistry, so the GrantViolation contract renders only when the
    // filter is OUTERMOST — the inverted chain renders UnknownTool here instead.
    RecordingProvider provider = new(scope,
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "ungranted", "{}")])),
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c2", "scoped", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([scoped, ungranted]), scope);

    AgentRunOutcome outcome = await spawner.RunAsync(
        Child(Anchor, effectiveTools: "scoped"), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);

    // The denied call was refused in the filter's own structured words (R1.3) —
    // never the plain UnknownTool line: the run's second provider call carries
    // exactly one tool result, the answer to the denied probe.
    Message denial = Assert.Single(provider.Requests[1].Messages,
        m => m.Role == Role.Tool);
    Assert.Equal(GrantViolation.For("ungranted"), denial.Content);

    // The granted call was served by the RE-ROOTED sentinel: the anchored view sits
    // BENEATH the filter (Filtered(Anchored(session)), not Anchored(Filtered(...)));
    // the ungranted tool never executed.
    Assert.Equal(Anchor, scoped.RootedAtArg);
    Assert.Equal([SentinelTool.SentinelExec], log.Entries);

    // The advertised surface is the filtered view of the anchored registry: only the
    // granted tool's definition reaches the provider (anchoring re-roots resolution,
    // never advertisement).
    string[] advertised = [.. provider.Requests[^1].Tools!.Select(t => t.Name)];
    Assert.Equal(["scoped"], advertised);
  }

  // ── fakes ────────────────────────────────────────────────────────────────────

  /// <summary>Which tool instance served a dispatch: the original or the re-rooted sentinel.</summary>
  private sealed class ExecLog
  {
    public List<string> Entries { get; } = [];
  }

  /// <summary>Workspace-scoped tool: RootedAt records the anchor and serves a sentinel,
  ///     so the run's own dispatch reveals which registry instance was in play.</summary>
  private sealed class ScopedFakeTool(string name, ExecLog log) : ITool, IWorkspaceScopedTool
  {
    public const string OriginalExec = "original";

    public ToolDefinition Definition { get; } = new(name, "desc", []);

    public string? RootedAtArg { get; private set; }

    public ITool RootedSentinel { get; } = new SentinelTool(name + "-rooted", log);

    public ITool RootedAt(string workspaceRoot)
    {
      RootedAtArg = workspaceRoot;
      return RootedSentinel;
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      log.Entries.Add(OriginalExec);
      return Task.FromResult(new ToolResult("ok", false));
    }
  }

  private sealed class SentinelTool(string name, ExecLog log) : ITool
  {
    public const string SentinelExec = "sentinel";

    public ToolDefinition Definition { get; } = new(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      log.Entries.Add(SentinelExec);
      return Task.FromResult(new ToolResult("rooted", false));
    }
  }

  /// <summary>Scripted provider that samples the anchor scope at every provider call;
  ///     unscripted calls serve a plain success so short runs still terminate.</summary>
  private sealed class RecordingProvider : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new();
    private readonly RecordingScope _scope;

    public RecordingProvider(RecordingScope scope, params Result<ModelResponse>[] responses)
    {
      _scope = scope;
      foreach (Result<ModelResponse> response in responses)
      {
        _responses.Enqueue(response);
      }
    }

    public List<string?> ScopeSamples { get; } = [];
    public List<ModelRequest> Requests { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      ScopeSamples.Add(_scope.Current);
      Requests.Add(request);
      return Task.FromResult(_responses.Count > 0
          ? _responses.Dequeue()
          : Result.Success(new ModelResponse("done", [])));
    }
  }

  /// <summary>Simple settable scope standing in for the composition-wired one.</summary>
  private sealed class RecordingScope : IWorkspaceAnchorScope
  {
    public string? Current { get; set; }
  }
}

using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The ambient MCP grant scope (issue #108): a child whose contract carries a
///     resolved effective set runs with that set as the AMBIENT scope - the dispatch
///     tool re-checks resolved ids against it. The scope flows down the child's async
///     flow (its nested dispatches see it) and is restored after the run; a contract
///     without a resolved set leaves the ambient untouched (full-reach default).</summary>
public class SubAgentSpawnerMcpGrantScopeTests
{
  private sealed class ScopeProbeTool : ITool
  {
    public ToolDefinition Definition { get; } = new("probe", "captures the ambient scope",
        [], ["timeoutSeconds"]);

    public IMcpGrantScope? Observed { get; private set; }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      Observed = AmbientMcpGrantScope.Current;
      return Task.FromResult(new ToolResult("probed", false));
    }
  }

  private static AgentRecord Child(string? effectiveTools)
      => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", null, "do things",
          new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
          effectiveTools is null ? null : new SpawnContract(EffectiveTools: effectiveTools));

  private static (SubAgentSpawner Spawner, FakeAgentStore Store) MakeRunner(
      FakeProvider provider, FakeAgentStore store, IToolRegistry tools)
      => (new SubAgentSpawner(new SubAgentServices(
          new FakeModelProviderFactory(provider), store, tools,
          new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m/sub"))),
      store);

  [Fact]
  public async Task RunAsync_ResolvedContract_SetsAmbientScopeDuringRun()
  {
    FakeAgentStore store = new();
    ScopeProbeTool probe = new();
    (SubAgentSpawner spawner, FakeAgentStore _) = MakeRunner(
        new FakeProvider(
            Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "probe", "")])),
            Result.Success(new ModelResponse("done", []))), store,
        new ToolRegistry([probe]));

    _ = await spawner.RunAsync(Child("probe;mcp.github.*;mcp"), TestContext.Current.CancellationToken);

    Assert.NotNull(probe.Observed);
    Assert.Null(probe.Observed.RefusalFor("mcp.github.create_issue"));
    Assert.NotNull(probe.Observed.RefusalFor("mcp.gitlab.push"));
  }

  [Fact]
  public async Task RunAsync_NoResolvedSet_AmbientStaysUnset()
  {
    FakeAgentStore store = new();
    ScopeProbeTool probe = new();
    (SubAgentSpawner spawner, FakeAgentStore _) = MakeRunner(
        new FakeProvider(Result.Success(new ModelResponse("done", []))), store,
        new ToolRegistry([probe]));

    _ = await spawner.RunAsync(Child(null), TestContext.Current.CancellationToken);

    Assert.Null(probe.Observed);
  }

  [Fact]
  public async Task RunAsync_ScopeRestoredAfterRun()
  {
    FakeAgentStore store = new();
    (SubAgentSpawner spawner, FakeAgentStore _) = MakeRunner(
        new FakeProvider(Result.Success(new ModelResponse("done", []))), store,
        new ToolRegistry([]));

    _ = await spawner.RunAsync(Child("mcp.github.*;mcp"), TestContext.Current.CancellationToken);

    Assert.Null(AmbientMcpGrantScope.Current);
  }
}

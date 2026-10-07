using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>B4 over the namespace (issue #108): spawn validation measures MCP grants
///     over namespace reach - a pattern within the parent's reach spawns; a pattern
///     outside it (or bare mcp over a patternless parent) is a strict refusal, never
///     a clamp.</summary>
public class StartSpawnHandlerNamespaceGrantTests
{
  private const string FallbackModel = "openrouter/auto";

  private static AgentRecord Parent(int depth = 0, string? contractJson = null) => new(
      new AgentId(Guid.NewGuid()), null, depth, AgentStatus.Completed, null,
      "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report",
      Contract: contractJson);

  private static StartSpawnHandler MakeHandler(FakeAgentStore store, FakeAgentRuntime runtime,
      IReadOnlySet<string>? surface)
      => new(store, runtime, new SubAgentOptions(DefaultModel: "fallback-model"),
          new SpawnOptions(FallbackModel, ChildToolSurface: surface),
          windowSource: new FixedWindowSource());

  private static SpawnRequest Request(string? allow = null, string? deny = null)
  {
    Dictionary<string, string>? grants = allow is null && deny is null ? null : [];
    if (allow is not null)
    {
      grants![ToolGrantPolicy.AllowKey] = allow;
    }

    if (deny is not null)
    {
      grants![ToolGrantPolicy.DenyKey] = deny;
    }

    return new SpawnRequest("task", Model: "explicit-model",
        Contract: grants is null ? null : new SpawnContract(CapabilityGrants: grants));
  }

  [Fact]
  public async Task Execute_PatternWithinSessionSurface_SpawnsWithPatternAndRoot()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "read", "mcp" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "mcp.github.*"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    SpawnContract decoded = SpawnContract.Decode(saved.Contract!);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "mcp", "mcp.github.*" },
        decoded.DecodedEffectiveTools);
  }

  [Fact]
  public async Task Execute_PatternOutsideParentReach_FailsStrictly()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    // The parent holds a NARROW persisted reach (github only): gitlab widens.
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "mcp", "mcp.github.*" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "mcp.gitlab.*"),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error.Code);
    Assert.Contains("mcp.gitlab.*", result.Error.Message, StringComparison.Ordinal);
    Assert.Empty(store.Saved);
  }

  [Fact]
  public async Task Execute_BareMcpOverPatternedParent_InheritsParentPatterns()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "mcp", "mcp.github.*" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "mcp"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    SpawnContract decoded = SpawnContract.Decode(saved.Contract!);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "mcp", "mcp.github.*" },
        decoded.DecodedEffectiveTools);
  }

  [Fact]
  public async Task Execute_BareMcpWithoutParentReach_FailsStrictly()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "read", "exec" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "mcp"),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error.Code);
    Assert.Empty(store.Saved);
  }
}

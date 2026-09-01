using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>R1 command-side tests: the child's effective set is resolved from the
///     parent's persisted set (or the session surface), validated against widening,
///     and PERSISTED on the record's contract so resume/audit/the remote host see the
///     same agreement. Grandchild narrowing chains are a subset by construction (R1.5).</summary>
public class StartSpawnHandlerGrantTests
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
  public async Task Execute_WithGrants_PersistsResolvedEffectiveSet_OnTheContract()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read", "exec" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "web_fetch; read", deny: "exec"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.NotNull(saved.Contract);
    SpawnContract decoded = SpawnContract.Decode(saved.Contract);
    Assert.NotNull(decoded.DecodedEffectiveTools);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read" },
        decoded.DecodedEffectiveTools);
  }

  [Fact]
  public async Task Execute_GrandchildNarrowsFromParentsPersistedSet_SubsetByConstruction()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    // The grandparent surface is wide; the PARENT carries a persisted narrow set —
    // the grandchild must measure against THAT, not the session surface (R1.5).
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read", "exec", "write", "edit" });
    AgentRecord parent = Parent(depth: 1, contractJson: SpawnContract.Encode(new SpawnContract(EffectiveTools: "read;edit")));

    Result<AgentId> result = await handler.Execute(parent, Request(allow: "read"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    SpawnContract decoded = SpawnContract.Decode(saved.Contract!);
    // Grandchild effective ⊆ parent effective, even though the session surface is wider.
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "read" }, decoded.DecodedEffectiveTools);
    // Grandchild effective ⊆ parent effective (R1.5) — the chain narrows from the
    // parent's PERSISTED set, never the wider session surface.
    IReadOnlySet<string> parentSet = SpawnContract.Decode(parent.Contract!).DecodedEffectiveTools!;
    Assert.True(parentSet.IsSupersetOf(decoded.DecodedEffectiveTools!));
  }

  [Fact]
  public async Task Execute_AllowBeyondSessionSurface_FailsStrictly()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "read" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "read; exec"),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error.Code);
    Assert.Empty(store.Saved); // rejected before any persistence
  }

  [Fact]
  public async Task Execute_NoSurfaceAndNoParentResolution_GrantFailsStrictly()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime, surface: null);

    Result<AgentId> result = await handler.Execute(Parent(), Request(allow: "read"),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error.Code);
  }

  [Fact]
  public async Task Execute_DenyOnlyGrant_PersistsSurfaceMinusDenied()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime,
        new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read", "exec" });

    Result<AgentId> result = await handler.Execute(Parent(), Request(deny: "exec"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    SpawnContract decoded = SpawnContract.Decode(saved.Contract!);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read" },
        decoded.DecodedEffectiveTools);
  }
}

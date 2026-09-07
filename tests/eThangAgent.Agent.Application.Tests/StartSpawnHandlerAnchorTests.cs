using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>T5 anchor tests: an optional request WorkspaceRoot is validated against the
///     parent's effective root (the parent's OWN persisted anchor when it carries one,
///     else the session workspace), the FULL resolved path is persisted on the contract,
///     and the legacy no-anchor path stays byte-identical.</summary>
public class StartSpawnHandlerAnchorTests
{
  private const string FallbackModel = "openrouter/auto";

  private static AgentRecord Parent(int depth = 0, string? contractJson = null) => new(
      new AgentId(Guid.NewGuid()), null, depth, AgentStatus.Completed, null,
      "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report",
      Contract: contractJson);

  private static StartSpawnHandler MakeHandler(FakeAgentStore store, FakeAgentRuntime runtime,
      string? sessionRoot)
      => new(store, runtime, new SubAgentOptions(DefaultModel: "fallback-model"),
          new SpawnOptions(FallbackModel, WorkspaceRoot: sessionRoot),
          windowSource: new FixedWindowSource());

  private static SpawnRequest Anchored(string anchor, SpawnContract? contract = null)
      => new("task", Model: "explicit-model", Contract: contract, WorkspaceRoot: anchor);

  private static string TempRoot()
  {
    string root = Path.Combine(Path.GetTempPath(), "anchor-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(root);
    return root;
  }

  [Fact]
  public async Task Execute_AnchorInsideParent_Succeeds_AndPersistsResolvedAnchorOnContract()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      string inside = Path.Combine(sessionRoot, "worktrees", "w1");
      _ = Directory.CreateDirectory(inside);
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot);

      // A trailing separator exercises full resolution: the persisted anchor is the
      // canonical FULL path, never the raw request text.
      Result<AgentId> result = await handler.Execute(Parent(),
          Anchored(inside + Path.DirectorySeparatorChar), ct: TestContext.Current.CancellationToken);

      Assert.True(result.IsSuccess);
      AgentRecord saved = Assert.Single(store.Saved);
      Assert.Equal(inside, SpawnContract.Decode(saved.Contract!).WorkspaceRoot);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_MissingAnchorDirectory_FailsAnchorMissing()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      // Named but never created: existence is part of the anchor rule.
      string missing = Path.Combine(sessionRoot, "does-not-exist");
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot);

      Result<AgentId> result = await handler.Execute(Parent(), Anchored(missing),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("AnchorMissing", result.Error.Code);
      Assert.Empty(store.Saved); // rejected before any persistence
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_AnchorOutsideParentRoot_FailsAnchorInvalid()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    string outside = TempRoot();
    try
    {
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot);

      Result<AgentId> result = await handler.Execute(Parent(), Anchored(outside),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("AnchorInvalid", result.Error.Code);
      Assert.Empty(store.Saved);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
      Directory.Delete(outside, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_PrefixSiblingAnchor_FailsAnchorInvalid()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    // A sibling whose name merely extends the root's name: containment must be
    // segment-aware (root + separator), not a raw string prefix.
    string evil = sessionRoot + "-evil";
    try
    {
      _ = Directory.CreateDirectory(evil);
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot);

      Result<AgentId> result = await handler.Execute(Parent(), Anchored(evil),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("AnchorInvalid", result.Error.Code);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
      Directory.Delete(evil, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_NullAnchor_LeavesLegacyContractUnchanged()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot: null);
    SpawnContract contract = new(ResultSchema: "{}");
    SpawnRequest request = new("task", Model: "explicit-model", Contract: contract);
    string expectedJson = SpawnContract.Encode(contract)!;

    Result<AgentId> result = await handler.Execute(Parent(), request,
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    // Byte-identical legacy path: the persisted contract is exactly what the request carried.
    Assert.Equal(expectedJson, saved.Contract);
    Assert.Null(SpawnContract.Decode(saved.Contract!).WorkspaceRoot);
  }

  [Fact]
  public async Task Execute_AnchoredParent_ChildAnchorUnderParentAnchor_Succeeds()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      string w1 = Path.Combine(sessionRoot, ".worktrees", "w1");
      string sub = Path.Combine(w1, "sub");
      _ = Directory.CreateDirectory(sub);
      AgentRecord parent = Parent(contractJson: SpawnContract.Encode(new SpawnContract(WorkspaceRoot: w1)));
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot);

      Result<AgentId> result = await handler.Execute(parent, Anchored(sub),
          ct: TestContext.Current.CancellationToken);

      Assert.True(result.IsSuccess);
      AgentRecord saved = Assert.Single(store.Saved);
      Assert.Equal(sub, SpawnContract.Decode(saved.Contract!).WorkspaceRoot);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_AnchoredParent_ChildAnchorInsideSessionButOutsideParentAnchor_FailsAnchorInvalid()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      string w1 = Path.Combine(sessionRoot, ".worktrees", "w1");
      _ = Directory.CreateDirectory(w1);
      string other = Path.Combine(sessionRoot, "other");
      _ = Directory.CreateDirectory(other);
      // The parent's anchor narrows the measurable root: "other" is inside the session
      // workspace yet outside the parent's persisted anchor, so it must fail.
      AgentRecord parent = Parent(contractJson: SpawnContract.Encode(new SpawnContract(WorkspaceRoot: w1)));
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot);

      Result<AgentId> result = await handler.Execute(parent, Anchored(other),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("AnchorInvalid", result.Error.Code);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_AnchoredRequest_WithoutAnyRoot_FailsAnchorInvalid()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string anchor = TempRoot();
    try
    {
      // No parent contract anchor and no session workspace: an anchor cannot be
      // validated without a root — named refusal, never silent inheritance.
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot: null);

      Result<AgentId> result = await handler.Execute(Parent(), Anchored(anchor),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("AnchorInvalid", result.Error.Code);
      Assert.Empty(store.Saved);
    }
    finally
    {
      Directory.Delete(anchor, recursive: true);
    }
  }
}

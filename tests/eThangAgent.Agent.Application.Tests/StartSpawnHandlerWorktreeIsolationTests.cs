using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Spawn-time worktree isolation: an IsolateInWorktree request provisions a
///     real worktree through the domain seam and anchors the child's contract at the
///     provisioned path; combining the flag with an explicit anchor is refused; a
///     provisioning failure refuses the spawn before any persistence; flag-less
///     requests never touch the seam; an anchored parent provisions inside ITS anchor.</summary>
public class StartSpawnHandlerWorktreeIsolationTests
{
  private const string FallbackModel = "openrouter/auto";

  private static AgentRecord Parent(int depth = 0, string? contractJson = null) => new(
      new AgentId(Guid.NewGuid()), null, depth, AgentStatus.Completed, null,
      "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report",
      Contract: contractJson);

  private static StartSpawnHandler MakeHandler(FakeAgentStore store, FakeAgentRuntime runtime,
      string? sessionRoot, IWorktreeProvisioner? provisioner = null)
      => new(store, runtime, new SubAgentOptions(DefaultModel: "fallback-model"),
          new SpawnOptions(FallbackModel, WorkspaceRoot: sessionRoot),
          windowSource: new FixedWindowSource(), worktrees: provisioner);

  private static string TempRoot()
  {
    string root = Path.Combine(Path.GetTempPath(), "isolation-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(root);
    return root;
  }

  [Fact]
  public async Task Execute_IsolateInWorktree_ProvisionsWorktree_AndAnchorsContractAtItsPath()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      FakeWorktreeProvisioner provisioner = new();
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot, provisioner);

      Result<AgentId> result = await handler.Execute(Parent(),
          new SpawnRequest("task", Model: "explicit-model", Label: "Fix Docs", IsolateInWorktree: true),
          ct: TestContext.Current.CancellationToken);

      Assert.True(result.IsSuccess);
      (string repoRoot, string name) = Assert.Single(provisioner.Calls);
      Assert.Equal(sessionRoot, repoRoot);
      // The derived name carries the label-derived prefix, a fresh fragment, and obeys
      // the WorktreeName rule - the seam validates it again, the handler pre-shapes it.
      Assert.StartsWith("fix-docs-", name, StringComparison.Ordinal);
      Assert.Matches("^[a-z0-9-]{1,64}$", name);
      string expectedPath = Path.Combine(sessionRoot, ".worktrees", name);
      AgentRecord saved = Assert.Single(store.Saved);
      Assert.Equal(expectedPath, SpawnContract.Decode(saved.Contract!).WorkspaceRoot);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_IsolateInWorktree_WithExplicitAnchor_FailsInvalidSpawnRequest_BeforeProvisioning()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      string anchor = Path.Combine(sessionRoot, "anchored");
      _ = Directory.CreateDirectory(anchor);
      FakeWorktreeProvisioner provisioner = new();
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot, provisioner);

      Result<AgentId> result = await handler.Execute(Parent(),
          new SpawnRequest("task", Model: "explicit-model", IsolateInWorktree: true, WorkspaceRoot: anchor),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("InvalidSpawnRequest", result.Error.Code);
      Assert.Empty(provisioner.Calls);
      Assert.Empty(store.Saved);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_IsolateInWorktree_ProvisionerFailure_FailsSpawn_BeforePersistence()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string sessionRoot = TempRoot();
    try
    {
      FakeWorktreeProvisioner provisioner = new()
      {
        Failure = new DomainError("WorktreeExists", "A worktree already exists."),
      };
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot, provisioner);

      Result<AgentId> result = await handler.Execute(Parent(),
          new SpawnRequest("task", Model: "explicit-model", Label: "w", IsolateInWorktree: true),
          ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("WorktreeExists", result.Error.Code);
      // The side effect failed: no child record, no runtime start, nothing half-born.
      Assert.Empty(store.Saved);
      Assert.Empty(runtime.Started);
    }
    finally
    {
      Directory.Delete(sessionRoot, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_NoFlag_NeverTouchesTheProvisioner()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    FakeWorktreeProvisioner provisioner = new();
    StartSpawnHandler handler = MakeHandler(store, runtime, TempRoot(), provisioner);

    Result<AgentId> result = await handler.Execute(Parent(),
        new SpawnRequest("task", Model: "explicit-model", Label: "Fix Docs"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Empty(provisioner.Calls);
  }

  [Fact]
  public async Task Execute_IsolateInWorktree_AnchoredParent_ProvisionsInsideParentAnchor()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    string parentAnchor = TempRoot();
    try
    {
      FakeWorktreeProvisioner provisioner = new();
      StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot: null, provisioner);
      AgentRecord parent = Parent(contractJson: SpawnContract.Encode(new SpawnContract(WorkspaceRoot: parentAnchor)));

      Result<AgentId> result = await handler.Execute(parent,
          new SpawnRequest("task", Model: "explicit-model", Label: "w", IsolateInWorktree: true),
          ct: TestContext.Current.CancellationToken);

      Assert.True(result.IsSuccess);
      (string repoRoot, string name) = Assert.Single(provisioner.Calls);
      // Isolation measures against the parent's effective root, exactly like an
      // explicit anchor would - grandchild chains confine their worktrees.
      Assert.Equal(parentAnchor, repoRoot);
      AgentRecord saved = Assert.Single(store.Saved);
      Assert.Equal(Path.Combine(parentAnchor, ".worktrees", name),
          SpawnContract.Decode(saved.Contract!).WorkspaceRoot);
    }
    finally
    {
      Directory.Delete(parentAnchor, recursive: true);
    }
  }

  [Fact]
  public async Task Execute_IsolateInWorktree_WithoutAnyRoot_FailsAnchorInvalid()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    FakeWorktreeProvisioner provisioner = new();
    // No parent anchor and no session workspace: isolation has no root to provision
    // inside - the same named refusal an anchored request without a root gets.
    StartSpawnHandler handler = MakeHandler(store, runtime, sessionRoot: null, provisioner);

    Result<AgentId> result = await handler.Execute(Parent(),
        new SpawnRequest("task", Model: "explicit-model", Label: "w", IsolateInWorktree: true),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("AnchorInvalid", result.Error.Code);
    Assert.Empty(provisioner.Calls);
    Assert.Empty(store.Saved);
  }

  private sealed class FakeWorktreeProvisioner : IWorktreeProvisioner
  {
    public List<(string RepoRoot, string Name)> Calls { get; } = [];
    public DomainError? Failure { get; init; }

    public Task<Result<WorktreeProvision>> CreateAsync(string repoRoot, string name, CancellationToken ct = default)
    {
      Calls.Add((repoRoot, name));
      return Task.FromResult(Failure is { } failure
          ? Result.Failure<WorktreeProvision>(failure)
          : Result.Success(new WorktreeProvision(name,
              Path.Combine(repoRoot, ".worktrees", name), "worktree/" + name)));
    }
  }
}

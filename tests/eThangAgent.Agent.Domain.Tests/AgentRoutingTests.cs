using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>R2: cross-container routing through the link registry (R2.1), multi-hop
///     escalation with per-hop receipts (R2.2). The trust test (R2.4) lives in
///     AgentLinkRegistryTests; the persistence decision (R2.3: in-memory, final for
///     v1) is documentation, not behavior.</summary>
public class AgentRoutingTests
{
  private static AgentRecord Parent(int depth = 1)
      => AgentRecord.Spawned(AgentId.NewId(), null, depth, "m/sub", "parent", "task", DateTimeOffset.UtcNow);

  private sealed class FakeRuntime : IAgentRuntime
  {
    public List<(Guid Target, string Text)> Deliveries { get; } = [];
    public HashSet<Guid> Running { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not used")));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
    {
      if (!Running.Contains(id.Value))
      {
        return Result.Failure<bool>(new DomainError("NotRunning", $"agent '{id}' is not running."));
      }

      Deliveries.Add((id.Value, message.Text));
      return Result.Success(true);
    }

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  /// <summary>Ancestor lookups ride the store-backed queries: the test preloads the
  ///     whole chain so escalate can walk it.</summary>
  private sealed class FakeQueries(params AgentRecord[] records) : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(records.FirstOrDefault(r => r.Id == id) is { } record
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"agent '{id}' not found.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", "not used")));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<AgentRecord>>(new DomainError("Unused", "not exercised here")));
  }

  private sealed class FakeSpawnCommand : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("Unused", "not exercised here")));
  }

  private static (AgentCapabilityProvider Provider, FakeRuntime Runtime, AgentLinkRegistry Links) Make(AgentRecord parent, params AgentRecord[] chain)
  {
    FakeRuntime runtime = new();
    AgentLinkRegistry links = new();
    AgentCapabilityProvider provider = new(
        new FakeSpawnCommand(), new FakeQueries(chain), () => parent, runtime, links);
    return (provider, runtime, links);
  }

  [Fact]
  public async Task Route_UnknownName_FailsNotLinked()
  {
    (AgentCapabilityProvider provider, FakeRuntime _, AgentLinkRegistry _) = Make(Parent());

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ "{\"name\":\"ghost\",\"text\":\"hello\"}", TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotLinked]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Route_ConsentedLink_DeliversToTheLinkedAddress()
  {
    (AgentCapabilityProvider provider, FakeRuntime runtime, AgentLinkRegistry links) = Make(Parent());
    Guid peer = Guid.NewGuid();
    _ = links.Link("peer", "container-a", peer.ToString("D"), consented: true);
    _ = runtime.Running.Add(peer);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ "{\"name\":\"peer\",\"text\":\"status check\"}", TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"delivered to={peer:D} link=peer", result.Content);
    _ = Assert.Single(runtime.Deliveries);
    Assert.Equal(peer, runtime.Deliveries[0].Target);
    Assert.Equal("status check", runtime.Deliveries[0].Text);
  }

  [Fact]
  public async Task Route_RevokedThenUnknownTarget_FailsNotRunning()
  {
    (AgentCapabilityProvider provider, FakeRuntime _, AgentLinkRegistry links) = Make(Parent());
    Guid peer = Guid.NewGuid();
    _ = links.Link("peer", "container-a", peer.ToString("D"), consented: true);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ "{\"name\":\"peer\",\"text\":\"hello\"}", TestContext.Current.CancellationToken);

    // The link resolves (the address is known) but the target is not a live agent:
    // the delivery seam's NotRunning surfaces, never a silent drop (A3).
    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotRunning]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Escalate_OneHop_DeliversToParent_WithReceipt()
  {
    AgentRecord grandparent = Parent(depth: 0);
    AgentRecord parent = AgentRecord.Spawned(AgentId.NewId(), grandparent.Id, 1, "m/sub", "parent",
        "task", DateTimeOffset.UtcNow);
    (AgentCapabilityProvider provider, FakeRuntime runtime, AgentLinkRegistry _) = Make(parent, parent, grandparent);
    _ = runtime.Running.Add(grandparent.Id.Value);

    CapabilityInvocationResult result = await provider.InvokeAsync("escalate",
        /*lang=json,strict*/ "{\"text\":\"need help\"}", TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"hop=1 to={grandparent.Id} delivered", result.Content);
    _ = Assert.Single(runtime.Deliveries);
  }

  [Fact]
  public async Task Escalate_TwoHops_RendersPerHopReceipts()
  {
    AgentRecord root = Parent(depth: 0);
    AgentRecord mid = AgentRecord.Spawned(AgentId.NewId(), root.Id, 1, "m/sub", "mid", "task", DateTimeOffset.UtcNow);
    AgentRecord leaf = AgentRecord.Spawned(AgentId.NewId(), mid.Id, 2, "m/sub", "leaf", "task", DateTimeOffset.UtcNow);
    (AgentCapabilityProvider provider, FakeRuntime runtime, AgentLinkRegistry _) = Make(leaf, leaf, mid, root);
    _ = runtime.Running.Add(mid.Id.Value);
    _ = runtime.Running.Add(root.Id.Value);

    CapabilityInvocationResult result = await provider.InvokeAsync("escalate",
        /*lang=json,strict*/ "{\"text\":\"roll up\",\"hops\":2}", TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    string[] lines = result.Content.Split('\n');
    Assert.Equal(2, lines.Length);
    Assert.Equal($"hop=1 to={mid.Id} delivered", lines[0]);
    Assert.Equal($"hop=2 to={root.Id} delivered", lines[1]);
    Assert.Equal(2, runtime.Deliveries.Count);
  }

  [Fact]
  public async Task Escalate_BeyondRoot_ReportsReachedRoot()
  {
    AgentRecord root = Parent(depth: 0);
    AgentRecord leaf = AgentRecord.Spawned(AgentId.NewId(), root.Id, 1, "m/sub", "leaf", "task", DateTimeOffset.UtcNow);
    (AgentCapabilityProvider provider, FakeRuntime _, AgentLinkRegistry _) = Make(leaf, leaf, root);

    CapabilityInvocationResult result = await provider.InvokeAsync("escalate",
        /*lang=json,strict*/ "{\"text\":\"up\",\"hops\":3}", TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("reached=root", result.Content, StringComparison.Ordinal);
  }
}

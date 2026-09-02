using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>W4.1/W4.2: the broadcast steering actions. notify-subtree walks the
///     persisted parent links DOWNWARD (the same chain InterruptSubtree walks) and
///     delivers one message to every live descendant; notify-ancestors walks UPWARD
///     to the root with escalate's receipt semantics and no hop bound. Per-target
///     receipts render in escalate's exact format (FR-C7), failures have a surface
///     (A3), and delivery is push-only — settled or foreign ids report NotRunning
///     and are never retried (A1).</summary>
public class NotifyBroadcastTests
{
  private static AgentRecord Agent(AgentId? parent, int depth, string label)
      => AgentRecord.Spawned(AgentId.NewId(), parent, depth, "m/sub", label, "task", DateTimeOffset.UtcNow);

  private sealed class FakeRuntime : IAgentRuntime
  {
    public List<(Guid Target, string Text, MessageUrgency Urgency, string Sender)> Deliveries { get; } = [];
    public HashSet<Guid> Running { get; } = [];
    public int Capacity { get; set; } = int.MaxValue;

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not used")));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
    {
      if (!Running.Contains(id.Value))
      {
        return Result.Failure<bool>(new DomainError(MailboxErrors.NotRunning, $"agent '{id}' is not running."));
      }

      if (Deliveries.Count >= Capacity)
      {
        return Result.Failure<bool>(new DomainError(MailboxErrors.Full, "mailbox is at capacity."));
      }

      Deliveries.Add((id.Value, message.Text, message.Urgency, message.Sender));
      return Result.Success(true);
    }

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  /// <summary>Children ride the store's children index — the same one the
  ///     spawner's tree teardown walks.</summary>
  private sealed class FakeQueries : IAgentQueries
  {
    private readonly Dictionary<Guid, AgentRecord> _records = [];
    private readonly Dictionary<Guid, List<AgentRecord>> _children = [];

    public FakeQueries(params AgentRecord[] records)
    {
      foreach (AgentRecord record in records)
      {
        _records[record.Id.Value] = record;
        if (record.ParentId is { } parent)
        {
          if (!_children.TryGetValue(parent.Value, out List<AgentRecord>? list))
          {
            _children[parent.Value] = list = [];
          }

          list.Add(record);
        }
      }
    }

    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(_records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"agent '{id}' not found.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", "not used")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
            _children.TryGetValue(parentId.Value, out List<AgentRecord>? kids) ? kids : []));
  }

  private sealed class FakeSpawnCommand : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("Unused", "not exercised here")));
  }

  private static (AgentCapabilityProvider Provider, FakeRuntime Runtime) Make(
      AgentRecord self, FakeQueries queries)
  {
    FakeRuntime runtime = new();
    AgentCapabilityProvider provider = new(
        new FakeSpawnCommand(), queries, () => self, runtime, links: null);
    return (provider, runtime);
  }

  [Fact]
  public async Task NotifySubtree_ReachesEveryLiveDescendant_WithPerTargetReceipts()
  {
    AgentRecord root = Agent(null, 0, "root");
    AgentRecord mid = Agent(root.Id, 1, "mid");
    AgentRecord leaf = Agent(mid.Id, 2, "leaf");
    (AgentCapabilityProvider provider, FakeRuntime runtime) = Make(root, new FakeQueries(root, mid, leaf));
    _ = runtime.Running.Add(mid.Id.Value);
    _ = runtime.Running.Add(leaf.Id.Value);

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-subtree",
        /*lang=json,strict*/ "{\"text\":\"roll up\",\"urgency\":\"attention\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    string[] lines = result.Content.Split('\n');
    Assert.Equal(3, lines.Length); // two receipts + the summary
    Assert.Equal($"hop=1 to={mid.Id} delivered", lines[0]);
    Assert.Equal($"hop=2 to={leaf.Id} delivered", lines[1]);
    Assert.Equal("reached=2 delivered=2", lines[2]);
    Assert.Equal(2, runtime.Deliveries.Count);
    Assert.All(runtime.Deliveries, d => Assert.Equal("roll up", d.Text));
    Assert.All(runtime.Deliveries, d => Assert.Equal(MessageUrgency.Attention, d.Urgency));
  }

  [Fact]
  public async Task NotifySubtree_SettledDescendantsReportNotRunning_NeverRetried()
  {
    AgentRecord root = Agent(null, 0, "root");
    AgentRecord live = Agent(root.Id, 1, "live");
    AgentRecord gone = Agent(root.Id, 1, "gone");
    (AgentCapabilityProvider provider, FakeRuntime runtime) = Make(root, new FakeQueries(root, live, gone));
    _ = runtime.Running.Add(live.Id.Value); // 'gone' is persisted but not running

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-subtree",
        /*lang=json,strict*/ "{\"text\":\"status check\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    string[] lines = result.Content.Split('\n');
    Assert.Equal(3, lines.Length);
    Assert.Contains(lines[0..2], line => line == $"hop=1 to={live.Id} delivered");
    Assert.Contains(lines[0..2], line => line.EndsWith(" NotRunning", StringComparison.Ordinal));
    Assert.Equal("reached=2 delivered=1", lines[2]);
    _ = Assert.Single(runtime.Deliveries); // push-only: one attempt, no retry
  }

  [Fact]
  public async Task NotifySubtree_MailboxFullSurfacesInTheReceipt()
  {
    AgentRecord root = Agent(null, 0, "root");
    AgentRecord child = Agent(root.Id, 1, "child");
    (AgentCapabilityProvider provider, FakeRuntime runtime) = Make(root, new FakeQueries(root, child));
    _ = runtime.Running.Add(child.Id.Value);
    runtime.Capacity = 0; // every delivery overflows

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-subtree",
        /*lang=json,strict*/ "{\"text\":\"hello\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError); // the broadcast succeeds; the FULL box has a surface
    string[] lines = result.Content.Split('\n');
    Assert.Equal($"hop=1 to={child.Id} MailboxFull", lines[0]);
    Assert.Equal("reached=1 delivered=0", lines[1]);
  }

  [Fact]
  public async Task NotifySubtree_EmptySubtree_SucceedsWithZeroSummary()
  {
    AgentRecord root = Agent(null, 0, "root");
    (AgentCapabilityProvider provider, FakeRuntime _) = Make(root, new FakeQueries(root));

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-subtree",
        /*lang=json,strict*/ "{\"text\":\"anyone?\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("reached=0 delivered=0", result.Content);
  }

  [Fact]
  public async Task NotifySubtree_DeepTree_FanOutOrder_DeepestHopsLabeled()
  {
    AgentRecord root = Agent(null, 0, "root");
    AgentRecord a = Agent(root.Id, 1, "a");
    AgentRecord b = Agent(root.Id, 1, "b");
    AgentRecord a1 = Agent(a.Id, 2, "a1");
    AgentRecord b1 = Agent(b.Id, 2, "b1");
    AgentRecord a1x = Agent(a1.Id, 3, "a1x");
    (AgentCapabilityProvider provider, FakeRuntime runtime) = Make(root,
        new FakeQueries(root, a, b, a1, b1, a1x));
    _ = runtime.Running.Add(a.Id.Value);
    _ = runtime.Running.Add(b.Id.Value);
    _ = runtime.Running.Add(a1.Id.Value);
    _ = runtime.Running.Add(b1.Id.Value);
    _ = runtime.Running.Add(a1x.Id.Value);

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-subtree",
        /*lang=json,strict*/ "{\"text\":\"spread\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("reached=5 delivered=5", result.Content.Split('\n')[^1]);
    Assert.Equal(5, runtime.Deliveries.Count);
    // hop = depth distance from the broadcaster: direct children are hop=1.
    Assert.Contains($"hop=1 to={a.Id} ", result.Content, StringComparison.Ordinal);
    Assert.Contains($"hop=3 to={a1x.Id} ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NotifyAncestors_WalksAllTheWayToTheRoot()
  {
    AgentRecord root = Agent(null, 0, "root");
    AgentRecord mid = Agent(root.Id, 1, "mid");
    AgentRecord leaf = Agent(mid.Id, 2, "leaf");
    (AgentCapabilityProvider provider, FakeRuntime runtime) = Make(leaf, new FakeQueries(leaf, mid, root));
    _ = runtime.Running.Add(mid.Id.Value);
    _ = runtime.Running.Add(root.Id.Value);

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-ancestors",
        /*lang=json,strict*/ "{\"text\":\"need help\",\"urgency\":\"urgent\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    string[] lines = result.Content.Split('\n');
    Assert.Equal(3, lines.Length);
    Assert.Equal($"hop=1 to={mid.Id} delivered", lines[0]);
    Assert.Equal($"hop=2 to={root.Id} delivered", lines[1]);
    Assert.Equal("reached=root delivered=2", lines[2]);
    Assert.All(runtime.Deliveries, d => Assert.Equal(MessageUrgency.Urgent, d.Urgency));
  }

  [Fact]
  public async Task NotifyAncestors_AtRoot_ReportsZeroTargetsWithoutError()
  {
    AgentRecord root = Agent(null, 0, "root");
    (AgentCapabilityProvider provider, FakeRuntime _) = Make(root, new FakeQueries(root));

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-ancestors",
        /*lang=json,strict*/ "{\"text\":\"up\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("reached=root delivered=0", result.Content);
  }

  [Fact]
  public async Task NotifyAncestors_PrunedAncestorRow_WalkTerminatesAtRoot()
  {
    // A walked-up parent whose row was purged: the receipt hop lines past the gap are
    // unreadable (no id to name), so the walk stops cleanly and reached=root closes.
    AgentRecord root = Agent(null, 0, "root");
    AgentRecord leaf = Agent(root.Id, 1, "leaf");
    (AgentCapabilityProvider provider, FakeRuntime runtime) = Make(leaf, new FakeQueries(leaf));
    _ = runtime.Running.Add(root.Id.Value);

    CapabilityInvocationResult result = await provider.InvokeAsync("notify-ancestors",
        /*lang=json,strict*/ "{\"text\":\"up\"}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    string[] lines = result.Content.Split('\n');
    Assert.Equal(2, lines.Length); // the gap's NotRunning receipt, then the closer
    Assert.Equal($"hop=1 to={root.Id} delivered", lines[0]);
    Assert.Equal("reached=root delivered=1", lines[1]);
    _ = Assert.Single(runtime.Deliveries);
  }

  [Fact]
  public async Task NotifyActions_WithoutRuntime_FailNotAvailable()
  {
    AgentRecord root = Agent(null, 0, "root");
    AgentCapabilityProvider provider = new(
        new FakeSpawnCommand(), new FakeQueries(root), () => root, runtime: null, links: null);

    CapabilityInvocationResult subtree = await provider.InvokeAsync("notify-subtree",
        /*lang=json,strict*/ "{\"text\":\"x\"}", TestContext.Current.CancellationToken);
    CapabilityInvocationResult ancestors = await provider.InvokeAsync("notify-ancestors",
        /*lang=json,strict*/ "{\"text\":\"x\"}", TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [NotAvailable]", subtree.Content, StringComparison.Ordinal);
    Assert.StartsWith("Error [NotAvailable]", ancestors.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ "{\"text\":\"\"}", "text")]                 // empty text
  [InlineData("{}", "text")] // plain braces: not JSON-shaped, analyzer-safe                                    // missing text
  [InlineData(/*lang=json,strict*/ "{\"text\":\"x\",\"hops\":2}", "unknown parameter")] // foreign argument
  [InlineData(/*lang=json,strict*/ "{\"text\":\"x\",\"urgency\":\"now\"}", "urgency")] // out-of-range urgency
  public async Task NotifyActions_ArgumentValidation_IsStrict(string json, string expectedFragment)
  {
    AgentRecord root = Agent(null, 0, "root");
    (AgentCapabilityProvider provider, FakeRuntime _) = Make(root, new FakeQueries(root));

    CapabilityInvocationResult subtree = await provider.InvokeAsync("notify-subtree",
        json, TestContext.Current.CancellationToken);
    CapabilityInvocationResult ancestors = await provider.InvokeAsync("notify-ancestors",
        json, TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [InvalidActionInput]", subtree.Content, StringComparison.Ordinal);
    Assert.StartsWith("Error [InvalidActionInput]", ancestors.Content, StringComparison.Ordinal);
    Assert.Contains(expectedFragment, subtree.Content, StringComparison.Ordinal);
    Assert.Contains(expectedFragment, ancestors.Content, StringComparison.Ordinal);
  }
}

using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #93: agent.status reports a RUNNING child's latest mid-run activity —
///     the phase and label of its last progress beat plus the age of that beat — so a
///     parent watching a long run learns what the child is DOING instead of only that it
///     is running. The suffix is additive: every other status, and any wiring with no
///     ledger, renders the pre-existing line byte-identically.</summary>
public class AgentStatusProgressTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class FakeQueries(AgentRecord record) : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotComplete", "running")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  private sealed class NoopSpawnCommand : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentId.NewId()));
  }

  private static AgentRecord Child(AgentStatus status) =>
      AgentRecord.Spawned(AgentId.NewId(), null, 1, "prov/child", null, "child task", T0) with
      {
        Status = status,
        CompletedAt = status is AgentStatus.Running ? null : T0,
      };

  private static AgentCapabilityProvider Provider(AgentRecord record, ChildProgressLedger? ledger, TimeProvider? clock = null)
      => new(new AgentProviderServices(new NoopSpawnCommand(), new FakeQueries(record),
          () => record, Progress: ledger, Clock: clock));

  [Fact]
  public async Task Running_WithABeat_RendersPhaseLabelAndAge()
  {
    AgentRecord child = Child(AgentStatus.Running);
    ChildProgressLedger ledger = new();
    ledger.OnEvent(new ChildProgressEvent(child.Id, T0, ChildPhase.ToolExec, "tool:exec"));
    StubClock clock = new(T0.AddSeconds(42));

    CapabilityInvocationResult result = await Provider(child, ledger, clock)
        .InvokeAsync("status", $"{{\"id\":\"{child.Id}\"}}", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal($"id={child.Id} status=running progress phase=tool-exec label=tool:exec age=42s", result.Content);
  }

  [Fact]
  public async Task Running_WithNoBeat_RendersTheLegacyLine()
  {
    AgentRecord child = Child(AgentStatus.Running);

    CapabilityInvocationResult result = await Provider(child, new ChildProgressLedger(), new StubClock(T0))
        .InvokeAsync("status", $"{{\"id\":\"{child.Id}\"}}", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal($"id={child.Id} status=running", result.Content);
  }

  [Fact]
  public async Task NoLedgerWired_RendersTheLegacyLine()
  {
    AgentRecord child = Child(AgentStatus.Running);

    CapabilityInvocationResult result = await Provider(child, ledger: null)
        .InvokeAsync("status", $"{{\"id\":\"{child.Id}\"}}", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal($"id={child.Id} status=running", result.Content);
  }

  [Theory]
  [InlineData(AgentStatus.Completed)]
  [InlineData(AgentStatus.Queued)]
  [InlineData(AgentStatus.Interrupted)]
  public async Task NonRunningStatus_NeverCarriesTheSuffix(AgentStatus status)
  {
    AgentRecord child = Child(status);
    ChildProgressLedger ledger = new();
    // A stale beat for a record that is no longer running: the suffix must not appear.
    ledger.OnEvent(new ChildProgressEvent(child.Id, T0, ChildPhase.ToolExec, "tool:exec"));

    CapabilityInvocationResult result = await Provider(child, ledger, new StubClock(T0.AddSeconds(5)))
        .InvokeAsync("status", $"{{\"id\":\"{child.Id}\"}}", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.DoesNotContain("progress", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FailedStatus_KeepsItsReasonSuffixAndNoProgress()
  {
    AgentRecord child = Child(AgentStatus.Failed) with { FailureReason = AgentFailureReason.Hung };
    ChildProgressLedger ledger = new();
    ledger.OnEvent(new ChildProgressEvent(child.Id, T0, ChildPhase.ToolExec, "tool:exec"));

    CapabilityInvocationResult result = await Provider(child, ledger, new StubClock(T0.AddSeconds(5)))
        .InvokeAsync("status", $"{{\"id\":\"{child.Id}\"}}", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal($"id={child.Id} status=failed reason=hung", result.Content);
  }
}

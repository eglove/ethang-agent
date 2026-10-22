using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #94: agent.cancel interrupts a running child by id through the
///     runtime's explicit-interrupt path and returns a receipt. An unknown id fails
///     NotFound; a settled child refuses NotRunning (nothing to cancel).</summary>
public class AgentCapabilityCancelTests
{
  [Fact]
  public async Task Cancel_RunningChild_InterruptsAndReturnsReceipt()
  {
    StubRuntime runtime = new();
    AgentCapabilityProvider provider = BuildProvider(runtime);
    AgentId childId = AgentCapabilityCancelTests.StubChildId;

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(childId), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Contains($"interrupted id={childId}", result.Content, StringComparison.Ordinal);
    Assert.Equal(childId.Value, Assert.Single(runtime.InterruptedIds));
  }

  [Fact]
  public async Task Cancel_UnknownId_FailsNotFound()
  {
    StubRuntime runtime = new();
    AgentCapabilityProvider provider = BuildProvider(runtime);
    AgentId missing = new(Guid.NewGuid());

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(missing), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotFound]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Cancel_SettledChild_RefusesNotRunning()
  {
    StubRuntime runtime = new();
    AgentCapabilityProvider provider = BuildProvider(runtime, completedChild: true);
    AgentId childId = AgentCapabilityCancelTests.StubChildId;

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(childId), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotRunning]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Cancel_WithoutRuntime_FailsNotAvailable()
  {
    AgentCapabilityProvider provider = BuildProvider(runtime: null);
    AgentId childId = new(Guid.NewGuid());

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(childId), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotAvailable]:", result.Content, StringComparison.Ordinal);
  }

  /// <summary>Builds the cancel JSON payload without raw-string escape hazards in the test file.</summary>
  private static string CancelJson(AgentId id)
  {
    const string quote = "\"";
    return "{" + quote + "id" + quote + ":" + quote + id.Value.ToString("D") + quote + "}";
  }

  private static AgentCapabilityProvider BuildProvider(StubRuntime? runtime, bool completedChild = false)
  {
    if (runtime is null)
    {
      return new AgentCapabilityProvider(new AgentProviderServices(
          new NoSpawn(), new StubQueries(null), NoParent, Clock: null));
    }

    AgentStatus status = completedChild ? AgentStatus.Completed : AgentStatus.Running;
    AgentRecord child = AgentRecord.Spawned(ChildId(), ParentId(), 1, "m", "child", "task", DateTimeOffset.UtcNow) with { Status = status };
    return new AgentCapabilityProvider(new AgentProviderServices(
        new NoSpawn(), new StubQueries(child), NoParent, runtime, Clock: null));
  }

  private static AgentRecord NoParent()
      => AgentRecord.Spawned(new AgentId(Guid.NewGuid()), null, 0, "m", "parent", "root", DateTimeOffset.UtcNow);

  private static AgentId ParentId() => new(Guid.NewGuid());

  /// <summary>The id the stub's child record carries; tests pass the SAME id in the
  ///     cancel JSON so the stub's GetStatus resolves it.</summary>
  public static AgentId StubChildId { get; } = new(Guid.NewGuid());

  private static AgentId ChildId() => StubChildId;

  private sealed class NoSpawn : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
      => throw new NotSupportedException("not exercised");
  }

  private sealed class StubRuntime : IAgentRuntime
  {
    public List<Guid> InterruptedIds { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
      => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not exercised")));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentId>(new DomainError("ResumeUnsupported", "not exercised")));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null)
    {
      if (childId is { } id)
      {
        InterruptedIds.Add(id.Value);
      }
    }
  }

  private sealed class StubQueries(AgentRecord? child) : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => child is not null && child.Id == id
            ? Task.FromResult(Result.Success(child))
            : Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", "no report")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}

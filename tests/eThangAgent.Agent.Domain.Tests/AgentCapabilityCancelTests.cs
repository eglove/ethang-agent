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
    AgentId agentId = StubChildId;

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(agentId), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Contains($"interrupted id={agentId}", result.Content, StringComparison.Ordinal);
    Assert.Equal(agentId.Value, Assert.Single(runtime.InterruptedIds));
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
    AgentId agentId = StubChildId;

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(agentId), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotRunning]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Cancel_WithoutRuntime_FailsNotAvailable()
  {
    AgentCapabilityProvider provider = BuildProvider(runtime: null);
    AgentId agentId = new(Guid.NewGuid());

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel",
        CancelJson(agentId), TestContext.Current.CancellationToken).ConfigureAwait(true);

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
      return new AgentCapabilityProvider(new AgentProviderServices(new StubQueries(null), Clock: null));
    }

    AgentStatus status = completedChild ? AgentStatus.Completed : AgentStatus.Running;
    AgentRecord child = AgentRecord.Started(AgentId(), "m", "child", "task", DateTimeOffset.UtcNow) with { Status = status };
    return new AgentCapabilityProvider(new AgentProviderServices(new StubQueries(child), runtime, Clock: null));
  }

  /// <summary>The id the stub's child record carries; tests pass the SAME id in the
  ///     cancel JSON so the stub's GetStatus resolves it.</summary>
  public static AgentId StubChildId { get; } = new(Guid.NewGuid());

  private static AgentId AgentId() => StubChildId;

  private sealed class StubRuntime : IAgentRuntime
  {
    public List<Guid> InterruptedIds { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
      => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not exercised")));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentId>(new DomainError("ResumeUnsupported", "not exercised")));

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? agentId = null)
    {
      if (agentId is { } id)
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

  }
}

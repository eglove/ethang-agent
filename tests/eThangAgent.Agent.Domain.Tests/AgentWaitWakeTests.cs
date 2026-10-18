using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The provider-level wait contract: default filter + always-on user wake,
///     the wakeOn vocabulary, strict argument validation, the verbatim woken
///     annotation, unchanged settlement rendering, and the legacy path when nothing
///     is wired.</summary>
public class AgentWaitWakeTests
{
  private sealed class FakeWakeSource : IWaitWakeSource
  {
    public Action<WakeSignal>? Observer { get; private set; }

    public IDisposable Subscribe(Action<WakeSignal> observer)
    {
      Observer = observer;
      return new FakeLease(this);
    }


    private sealed class FakeLease(FakeWakeSource owner) : IDisposable
    {
      public void Dispose() => owner.Observer = null;
    }
  }

  private sealed class SettleWhenSignaledRuntime : IAgentRuntime
  {
    public TaskCompletionSource<Result<AgentRunOutcome>> Settle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Settle.Task;

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  private static AgentCapabilityProvider MakeProvider(IAgentRuntime runtime, IWaitWakeSource? source = null)
  {
    FakeSpawnCommand command = new(Result.Success(AgentId.NewId()), []);
    return new AgentCapabilityProvider(new AgentProviderServices(command, new FakeQueries(),
        () => AgentRecord.Spawned(AgentId.NewId(), null, 0, "prov", null, "root", DateTimeOffset.UtcNow),
        runtime, WakeSource: source));
  }

  // Minimal fakes (the provider tests' own fakes are nested in that class):
  private sealed class FakeSpawnCommand(Result<AgentId> reply, List<(AgentRecord Parent, SpawnRequest Request)> calls) : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
    {
      calls.Add((parent, request));
      return Task.FromResult(reply);
    }
  }

  private sealed class FakeQueries : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "no agent.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", "no agent.")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  [Fact]
  public async Task Wait_NoParams_WakesOnUserMessage()
  {
    FakeWakeSource source = new();
    SettleWhenSignaledRuntime runtime = new();
    AgentCapabilityProvider provider = MakeProvider(runtime, source);
    AgentId child = AgentId.NewId();

    Task<CapabilityInvocationResult> call = provider.InvokeAsync("wait", $"{{\"id\":\"{child}\"}}", ct: TestContext.Current.CancellationToken);
    source.Observer!.Invoke(new WakeSignal(WakeReason.UserMessage, child, DateTimeOffset.UtcNow, "user message waiting"));
    CapabilityInvocationResult result = await call.ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("[wait: woken reason=user-message]", result.Content, StringComparison.Ordinal);
    Assert.Contains($"id={child} status=running", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_WakeOnAll_WakesOnEverySignalKind()
  {
    FakeWakeSource source = new();
    SettleWhenSignaledRuntime runtime = new();
    AgentCapabilityProvider provider = MakeProvider(runtime, source);
    AgentId child = AgentId.NewId();

    Task<CapabilityInvocationResult> call = provider.InvokeAsync("wait", $"{{\"id\":\"{child}\",\"wakeOn\":\"all\"}}", ct: TestContext.Current.CancellationToken);
    source.Observer!.Invoke(new WakeSignal(WakeReason.StreamStall, child, DateTimeOffset.UtcNow, "stream-stalled quiet=130s last=content"));
    CapabilityInvocationResult result = await call.ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("reason=stream-stall", result.Content, StringComparison.Ordinal);
    Assert.Contains("stream-stalled quiet=130s", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_InvalidWakeOnToken_FailsInvalidActionInput()
  {
    AgentCapabilityProvider provider = MakeProvider(new SettleWhenSignaledRuntime(), new FakeWakeSource());
    AgentId child = AgentId.NewId();

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $"{{\"id\":\"{child}\",\"wakeOn\":\"banana\"}}", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("banana", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_ZeroTimeout_FailsInvalidActionInput()
  {
    AgentCapabilityProvider provider = MakeProvider(new SettleWhenSignaledRuntime(), new FakeWakeSource());
    AgentId child = AgentId.NewId();

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $"{{\"id\":\"{child}\",\"timeoutSeconds\":0}}", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("positive", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_Settled_RendersOutcomeUnchanged()
  {
    AgentId child = AgentId.NewId();
    SettleOnDemandRuntime runtime = new(new AgentRunOutcome(child, AgentStatus.Completed, null, "the awaited report", "prov", 1));
    AgentCapabilityProvider provider = MakeProvider(runtime, new FakeWakeSource());

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $"{{\"id\":\"{child}\"}}", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal("the awaited report", result.Content);
  }

  [Fact]
  public async Task Wait_UnknownId_FailsNotFound()
  {
    AgentCapabilityProvider provider = MakeProvider(new NotFoundRuntime(), new FakeWakeSource());

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $"{{\"id\":\"{AgentId.NewId()}\"}}", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("NotFound", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_NoSourceWired_BehavesAsToday()
  {
    AgentId child = AgentId.NewId();
    SettleOnDemandRuntime runtime = new(new AgentRunOutcome(child, AgentStatus.Completed, null, "legacy report", "prov", 1));
    AgentCapabilityProvider provider = MakeProvider(runtime, source: null);

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $"{{\"id\":\"{child}\"}}", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal("legacy report", result.Content);
  }

  private sealed class SettleOnDemandRuntime(AgentRunOutcome outcome) : IAgentRuntime
  {
    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(outcome));

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  private sealed class NotFoundRuntime : IAgentRuntime
  {
    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "no such agent.")));

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }
}

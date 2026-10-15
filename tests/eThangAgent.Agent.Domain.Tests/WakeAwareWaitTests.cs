using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The wake-aware wait core: settle-vs-wake racing, the always-on user
///     wake, filter behavior, digest accumulation, timeout expiry, and cancellation.
///     The settle awaiter is a delegate so tests drive it with a TCS.</summary>
public class WakeAwareWaitTests
{
  private sealed class FakeWakeSource : IWaitWakeSource
  {
    public Action<WakeSignal>? Observer { get; private set; }

    public IDisposable Subscribe(Action<WakeSignal> observer)
    {
      Observer = observer;
      return new FakeLease(this);
    }

    public void Push(WakeSignal signal) => Observer?.Invoke(signal);

    private sealed class FakeLease(FakeWakeSource owner) : IDisposable
    {
      public void Dispose() => owner.Observer = null;
    }
  }

  private static WakeSignal Signal(WakeReason reason, AgentId childId) =>
      new(reason, childId, DateTimeOffset.UtcNow, reason + " digest");

  private static WakeFilter Parsed(string raw)
  {
    Result<WakeFilter> parsed = WakeFilter.Parse(raw);
    Assert.True(parsed.IsSuccess);
    return parsed.Value;
  }

  private static AgentRunOutcome Settled(AgentId id) =>
      new(id, AgentStatus.Completed, null, "the report", "prov/child", 1);

  [Fact]
  public async Task SettlesNormally_WhenNothingWakes()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);

    Task<WaitResult> run = wait.RunAsync(new WaitSpec(id, Parsed("message"), null), TestContext.Current.CancellationToken);
    settle.SetResult(Result.Success(Settled(id)));
    WaitResult result = await run.ConfigureAwait(true);

    Assert.True(result.Settled);
    Assert.Equal("the report", result.Outcome!.Report);
  }

  [Fact]
  public async Task UserMessage_Wakes_EvenWhenFilterExcludesIt()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);

    Task<WaitResult> run = wait.RunAsync(new WaitSpec(id, Parsed("progress"), null), TestContext.Current.CancellationToken);
    source.Push(Signal(WakeReason.UserMessage, id));
    WaitResult result = await run.ConfigureAwait(true);

    Assert.False(result.Settled);
    Assert.Equal(WakeReason.UserMessage, result.Reason);
    _ = Assert.Single(result.DigestLines);
  }

  [Fact]
  public async Task FilteredSignal_DoesNotWake()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);

    Task<WaitResult> run = wait.RunAsync(new WaitSpec(id, Parsed("message"), null), TestContext.Current.CancellationToken);
    source.Push(Signal(WakeReason.Progress, id));
    settle.SetResult(Result.Success(Settled(id)));
    WaitResult result = await run.ConfigureAwait(true);

    Assert.True(result.Settled); // the filtered signal never woke the wait
  }

  [Fact]
  public async Task MultipleSignals_AccumulateInOrder()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);

    Task<WaitResult> run = wait.RunAsync(new WaitSpec(id, Parsed("all"), null), TestContext.Current.CancellationToken);
    source.Push(Signal(WakeReason.Progress, id));
    source.Push(Signal(WakeReason.Idle, id));
    source.Push(Signal(WakeReason.StreamStall, id));
    WaitResult result = await run.ConfigureAwait(true);

    Assert.False(result.Settled);
    Assert.Equal(WakeReason.Progress, result.Reason); // the FIRST signal's reason
    Assert.Equal(3, result.DigestLines.Count);
    Assert.Contains("Progress digest", result.DigestLines[0], StringComparison.Ordinal);
    Assert.Contains("StreamStall digest", result.DigestLines[2], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Timeout_Expires_WithRunningStatus()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);

    WaitResult result = await wait.RunAsync(
        new WaitSpec(id, Parsed("all"), TimeSpan.FromMilliseconds(50)), TestContext.Current.CancellationToken);

    Assert.False(result.Settled);
    Assert.Empty(result.DigestLines);
  }

  [Fact]
  public async Task Settle_RacingWake_SettlementWins()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);

    Task<WaitResult> run = wait.RunAsync(new WaitSpec(id, Parsed("all"), null), TestContext.Current.CancellationToken);
    settle.SetResult(Result.Success(Settled(id)));
    WaitResult result = await run.ConfigureAwait(true);
    source.Push(Signal(WakeReason.Progress, id)); // after settlement: dropped

    Assert.True(result.Settled);
  }

  [Fact]
  public async Task Cancelled_Propagates()
  {
    AgentId id = AgentId.NewId();
    FakeWakeSource source = new();
    TaskCompletionSource<Result<AgentRunOutcome>> settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WakeAwareWait wait = new(source, _ => settle.Task, TimeProvider.System);
    using CancellationTokenSource cts = new();

    Task<WaitResult> run = wait.RunAsync(new WaitSpec(id, Parsed("all"), null), cts.Token);
    await cts.CancelAsync();

    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).ConfigureAwait(true);
  }
}

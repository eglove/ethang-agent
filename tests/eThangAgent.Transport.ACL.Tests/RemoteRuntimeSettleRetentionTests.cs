using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The remote runtime's settle-retention contract (W1.1 prerequisite): a
///     remote run may settle MORE THAN ONCE — the host watchdog's wrap-up retry
///     settles the interrupted attempt (breach 1) before the FINAL terminal settle
///     (breach 2, Failed(Hung)) — and a waiter observing the run at any point must
///     read a well-formed outcome, never NotFound and never a silently dropped
///     envelope. Mirrors the in-process runtime's Settle (T3 ruling: completed
///     sources are retained as outcome records; late waiters see the LATEST outcome).</summary>
public class RemoteRuntimeSettleRetentionTests
{
  private static async Task<(NamedPipeChildTransport App, NamedPipeChildTransport Host)> ConnectAsync()
  {
    string pipeName = "ethang-settle-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> serverTask = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport host = await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (app, host);
  }

  /// <summary>Production order: the app's Start registers the settle source BEFORE the
  ///     host runs the child and emits settles. The fake host never acts on the start
  ///     envelope; nothing on the send path waits for its ack.</summary>
  private static async Task<RemoteAgentRuntime> StartedRuntimeAsync(
      NamedPipeChildTransport app, AgentId child)
  {
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    Result<AgentId> started = await runtime.Start(
        AgentRecord.Spawned(child, null, 1, "m/sub", "hung", "task", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);
    return runtime;
  }

  [Fact]
  public async Task LateWaiter_ObservesTheLatestOutcome_NotNotFound()
  {
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    AgentId child = new(Guid.NewGuid());
    RemoteAgentRuntime runtime = await StartedRuntimeAsync(app, child).ConfigureAwait(true);

    // First settle: the wrap-up retry's interrupted attempt (breach 1). The FIRST
    // waiter observes the attempt's outcome.
    await host.SendAsync(new TransportEnvelope("settle",
        JsonSerializer.Serialize(new SettleNotice(child.Value, "Interrupted", "Interrupted", "attempt cut")), 1),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<AgentRunOutcome> first = await runtime.WhenSettledAsync(child, TestContext.Current.CancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.Equal(AgentStatus.Interrupted, first.Value.Status);

    // Second settle for the SAME id: the run's final terminal outcome (breach 2) —
    // must REPLACE the recorded outcome, never be dropped. The pump handles the
    // envelope asynchronously: bounded poll (doctrine: no sleep-poll on internal state).
    await host.SendAsync(new TransportEnvelope("settle",
        JsonSerializer.Serialize(new SettleNotice(child.Value, "Failed", "Hung", "Error [Hung]: terminated")), 2),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<AgentRunOutcome> latest = Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not yet"));
    for (int i = 0; i < 100 && (!latest.IsSuccess || latest.Value.Status is AgentStatus.Interrupted); i++)
    {
      latest = await runtime.WhenSettledAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (latest.IsSuccess && latest.Value.Status is AgentStatus.Failed)
      {
        break;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.True(latest.IsSuccess, "the final settle envelope was dropped — a late waiter saw NotFound");
    Assert.Equal(AgentStatus.Failed, latest.Value.Status);
    Assert.Equal(AgentFailureReason.Hung, latest.Value.Reason);
    await app.DisposeAsync().ConfigureAwait(true);
    await host.DisposeAsync().ConfigureAwait(true);
  }

  [Fact]
  public async Task SettleForAnUnknownId_IsRecorded_NotLost()
  {
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    // A re-attach may deliver a settle for a child this runtime instance never started.
    AgentId stranger = new(Guid.NewGuid());
    await host.SendAsync(new TransportEnvelope("settle",
        JsonSerializer.Serialize(new SettleNotice(stranger.Value, "Completed", null, "late settle")), 1),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The pump processes the envelope asynchronously: poll bounded, never sleep-poll.
    Result<AgentRunOutcome> outcome = Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not yet"));
    for (int i = 0; i < 100 && !outcome.IsSuccess; i++)
    {
      outcome = await runtime.WhenSettledAsync(stranger, TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (outcome.IsSuccess)
      {
        break;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.True(outcome.IsSuccess, "the late settle was never recorded");
    Assert.Equal(AgentStatus.Completed, outcome.Value.Status);
    await app.DisposeAsync().ConfigureAwait(true);
    await host.DisposeAsync().ConfigureAwait(true);
  }
}

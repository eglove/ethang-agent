using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application;

/// <summary>What the root watchdog needs to enact a decision (issue #301 D5). The watchdog
///     decides; the shell owns the session, so it supplies the two acts.
///     <para>WHY A PORT: a root session's steering inbox lives in the HOST process and its
///     tab lives in the app. Neither is reachable from the Agent Application layer, so the
///     watchdog asks rather than reaches.</para></summary>
public interface IRootSessionHost
{
  /// <summary>Posts the nudge into the root session's inbox. False when the session is not
  ///     reachable (closed, or its host is gone), so a nudge that never landed does not
  ///     consume the wrap-up budget.</summary>
  /// <param name="sessionId">The root session to nudge.</param>
  /// <param name="text">The nudge text.</param>
  bool TryNudge(AgentId sessionId, string text);

  /// <summary>Parks the chain this session is executing, naming the step and the reason.
  ///     Best effort: a park that cannot be written is reported by the caller's audit row.</summary>
  /// <param name="sessionId">The session whose chain is parked.</param>
  /// <param name="reason">Why the chain is waiting for a human.</param>
  /// <param name="ct">Cancellation.</param>
  Task ParkAsync(AgentId sessionId, string reason, CancellationToken ct = default);
}

/// <summary>Supervises ONE root session's idleness (issue #301 D5): nudge once, then park.
///     <para>THIS IS NEW SUPERVISION, NOT A REUSE. The existing watchdog sweeps
///     <c>DescendantsOf(rootId)</c>, whose walk starts from a record's <c>ParentId</c>, so the
///     root is never in the set; supervisors are minted per CHILD RUN inside the runtime's
///     <c>BeginRun</c>; and orphan repair exempts the root explicitly. The POLICY is shared
///     (<see cref="RootSupervisionPolicy"/> reads the same attempt budget); the supervision
///     had to be built.</para>
///     <para>THE CLOCK IS THE ROOT LOOP'S OWN TURN FACTS (<see cref="RootTurnActivity"/>),
///     never <see cref="ChildProgressEvent"/> - #305 retired the child runtime and #316
///     retires the sibling machinery, so a clock fed by child beats would stop ticking
///     silently. It is also never a bare wall-clock timer, the mechanism <c>ChildTimeout</c>
///     was deleted for.</para>
///     <para>ONLY A CHAIN STEP IS SUPERVISED. A session started by hand owes no completion, so
///     it gets no watchdog: constructing one without an assignment makes every tick a no-op,
///     which is the honest shape rather than nudging a session that was never assigned work.</para>
///     <para>NEVER THROWS: a failing tick degrades to an audit row, exactly like
///     <see cref="AgentWatchdog"/>.</para></summary>
public sealed class RootSessionWatchdog(AgentId sessionId, RootSessionSupervisor supervisor,
    ChainStepAssignment? assignment, IRootSessionHost host, WatchdogPolicy policy,
    IWatchdogEventStore events, TimeProvider clock) : IWatchdogTicker
{
  private readonly IRootSessionHost _host = host ?? throw new ArgumentNullException(nameof(host));
  private readonly WatchdogPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
  private readonly IWatchdogEventStore _events = events ?? throw new ArgumentNullException(nameof(events));
  private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

  /// <summary>The supervisor this ticker reads, so the shell can feed it the root loop's own
  ///     turn facts. Null-assignment watchdogs keep it too: a session may be assigned a step
  ///     after it starts, and the clock is useful either way.</summary>
  public RootSessionSupervisor Supervisor { get; } = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

  public async Task TickAsync(CancellationToken ct = default)
  {
    try
    {
      if (assignment is not { } assigned)
      {
        return; // not a chain step: nothing to complete, nothing to nudge
      }

      DateTimeOffset now = _clock.GetUtcNow();
      if (Supervisor.CheckIdle(_policy.IdleThreshold, now) is not { } alert)
      {
        return;
      }

      await AuditAsync(WatchdogEventKind.HungDetected,
          $"root idle {(int)alert.IdleAge.TotalMinutes}m; phase {alert.Phase} {alert.Label}", ct)
          .ConfigureAwait(false);

      if (RootSupervisionPolicy.Decide(Supervisor.WrapUpAttempts, _policy.MaxWrapUpAttempts)
          == RootSupervisionDecision.Nudge)
      {
        await NudgeAsync(assigned, ct).ConfigureAwait(false);
        return;
      }

      await ParkAsync(assigned, alert, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    // Named decision (CA1031): the watchdog is a fault boundary - a failing tick must never
    // take down the loop that guards every other session.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      await AuditAsync(WatchdogEventKind.WatchdogErrored, "root supervision fault: " + ex.Message, ct)
          .ConfigureAwait(false);
    }
  }

  /// <summary>Sends the nudge and spends one attempt. The attempt is recorded only when the
  ///     nudge actually landed: a session whose host is gone would otherwise burn its whole
  ///     budget on messages nobody received.</summary>
  private async Task NudgeAsync(ChainStepAssignment assignment, CancellationToken ct)
  {
    if (!_host.TryNudge(sessionId, RootSupervisionPolicy.Nudge(assignment.ChainId, assignment.Position)))
    {
      await AuditAsync(WatchdogEventKind.RetryDeferred,
          "root nudge could not be delivered; the session is unreachable", ct).ConfigureAwait(false);
      return;
    }

    Supervisor.RecordWrapUp();
    await AuditAsync(WatchdogEventKind.RetrySpawned,
        $"root nudge delivered for chain #{assignment.ChainId} step {assignment.Position}", ct)
        .ConfigureAwait(false);
  }

  /// <summary>Parks the chain: the nudge budget is spent and the session is still idle, so the
  ///     work is waiting for a human rather than broken (#303 D3). The reason names the step,
  ///     because 'nothing happened' and 'waiting on step 3' must not look alike.</summary>
  private async Task ParkAsync(ChainStepAssignment assignment, RootIdleAlert alert, CancellationToken ct)
  {
    await _host.ParkAsync(sessionId,
        $"chain #{assignment.ChainId} step {assignment.Position} went idle for {(int)alert.IdleAge.TotalMinutes}m "
        + $"after {Supervisor.WrapUpAttempts} nudge(s); the session never completed the step.", ct)
        .ConfigureAwait(false);
    await AuditAsync(WatchdogEventKind.TerminalReport,
        $"chain #{assignment.ChainId} parked at step {assignment.Position} after {Supervisor.WrapUpAttempts} nudge(s)", ct)
        .ConfigureAwait(false);
  }

  private async Task AuditAsync(WatchdogEventKind kind, string detail, CancellationToken ct)
    => _ = await _events.AppendAsync(new WatchdogEvent(Guid.NewGuid(), sessionId, kind, detail,
        Supervisor.WrapUpAttempts, null, _clock.GetUtcNow()), ct).ConfigureAwait(false);
}

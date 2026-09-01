using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>One watchdog sweep for ONE session root: descendants of the root in the shared
///     agent store, liveness via this container's heartbeat, decisions via the pure policy,
///     enactment through the runtime and store seams, audit via event rows. Never throws:
///     a failing sweep degrades to a rate-limited WatchdogErrored event. RSS sampling rides
///     the same tick, observe-only. Heartbeat-presence gate: a Running child with NO beat
///     entry is always Watch - the run belongs to another container or has not beaten yet.
///     The policy uses one injected TimeProvider for every time read in a tick.</summary>
public sealed class AgentWatchdog(AgentId rootId, WatchdogServices services) : IWatchdogTicker, IDisposable
{
  private readonly IAgentStore _store = services.Store;

  /// <summary>The supervisor registry feed (handoff item 2): progress events drive
  ///     supervisors' idle clocks and phases; settles retire supervisors. Subscribed
  ///     here so every tick sees fed facts; the lease disposes with the watchdog.
  ///     Without it, supervisors never hear from their children and any healthy child
  ///     outliving the idle threshold false-positives as hung.</summary>
  private readonly IDisposable? _feedLease =
      services.Supervisors is null ? null : services.ChildEventStream?.Subscribe(new SupervisorFeed(services.Supervisors));
  private DateTimeOffset? _rssBreachSince;
  private DateTimeOffset? _lastRssReport;
  private DateTimeOffset? _lastErrorReport;
  /// <summary>Settle-poll cadence: one second in production; tests inject a sub-second
  ///     cadence so deadline paths run fast while the iteration bound still gates the loop.</summary>
  private TimeSpan _settlePollInterval = TimeSpan.FromSeconds(1);

  public AgentWatchdog WithSettlePollInterval(TimeSpan interval)
  {
    _settlePollInterval = interval;
    return this;
  }

  public async Task TickAsync(CancellationToken ct = default)
  {
    try
    {
      await SampleRssAsync(ct).ConfigureAwait(false);
      await SuperviseRegisteredChildrenAsync(ct).ConfigureAwait(false);
      Result<IReadOnlyList<AgentRecord>> listed = await _store.ListAllAsync(ct).ConfigureAwait(false);
      if (!listed.IsSuccess)
      {
        return;
      }

      List<Guid> descendants = DescendantsOf(listed.Value);
      Dictionary<Guid, AgentRecord> byId = listed.Value.ToDictionary(r => r.Id.Value);
      foreach (Guid id in descendants)
      {
        try
        {
          await SweepOneAsync(byId[id], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          throw;
        }
        // Named decision (CA1031): the watchdog is a fault boundary - one child's sweep
        // failure must never take down the tick that guards the others.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
          await AppendAsync(new WatchdogEvent(Guid.NewGuid(), new AgentId(id),
              WatchdogEventKind.WatchdogErrored, ex.Message, 0, null,
              services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
        }
      }

      foreach (Guid id in descendants.Where(id => byId[id].Status is not AgentStatus.Running))
      {
        services.Heartbeat.Forget(new AgentId(id));
      }
    }
    catch (OperationCanceledException)
    {
      throw; // host shutdown ends the loop; nothing to record
    }
    // Named decision (CA1031): same fault boundary at tick scope; rate-limited.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      await RecordErrorAsync(ex.Message, ct).ConfigureAwait(false);
    }
  }

  private List<Guid> DescendantsOf(IReadOnlyList<AgentRecord> records)
  {
    Dictionary<Guid, Guid?> parents = records.ToDictionary(r => r.Id.Value, r => r.ParentId?.Value);
    List<Guid> result = [];
    foreach (AgentRecord record in records)
    {
      Guid? cursor = record.ParentId?.Value;
      while (cursor is { } current)
      {
        if (current == rootId.Value)
        {
          result.Add(record.Id.Value);
          break;
        }

        cursor = parents.GetValueOrDefault(current);
      }
    }

    return result;
  }

  /// <summary>Event-policy path (T5): iterate THIS session's registered supervisors,
  ///     raise idle alerts, and let the pure policy decide. Attempts come from the
  ///     supervisor's owned fact — no ledger counting, no store sweep. A RetryWrapUp
  ///     interrupt observes settlement through the runtime's WhenSettledAsync (bounded by
  ///     iteration on the injected clock), replacing the poll loop.</summary>
  private async Task SuperviseRegisteredChildrenAsync(CancellationToken ct)
  {
    if (services.Supervisors is null)
    {
      return; // legacy wiring: registry absent — sweep below keeps today's behavior
    }

    foreach (ChildSupervisor supervisor in services.Supervisors.All)
    {
      try
      {
        // Hard budget ceiling: one mechanism, two policies (D8) — same interrupt path as a
        // watchdog terminal decision, audited as BudgetExhausted.
        if (supervisor.HardCeilingReached)
        {
          services.Runtime.Interrupt(supervisor.ChildId);
          await AppendAsync(new WatchdogEvent(Guid.NewGuid(), supervisor.ChildId,
              WatchdogEventKind.TerminalReport, "budget hard ceiling reached", supervisor.Attempts, null,
              services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
          continue;
        }

        ChildIdleAlertEvent? alert = supervisor.CheckIdle(services.Policy.IdleThreshold);
        if (alert is null)
        {
          continue;
        }

        await AppendAsync(new WatchdogEvent(Guid.NewGuid(), alert.ChildId,
            WatchdogEventKind.HungDetected, "idle " + (int)alert.IdleAge.TotalMinutes + "m; phase " + alert.LastPhase,
            supervisor.Attempts, null, services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
        services.Runtime.Interrupt(alert.ChildId);
        AgentRecord settled = await AwaitSettledAsync(alert.ChildId, ct).ConfigureAwait(false);
        if (settled.Status is AgentStatus.Running)
        {
          continue; // cancel unobserved: next tick re-raises while still idle
        }

        if (settled.Status is AgentStatus.Completed)
        {
          continue; // finished while we watched
        }

        if (supervisor.Attempts >= services.Policy.MaxWrapUpAttempts)
        {
          await TerminalAsync(settled, supervisor.Attempts, alert.IdleAge, ct).ConfigureAwait(false);
        }
        else
        {
          await RetryAsync(settled, supervisor.Attempts + 1, ct).ConfigureAwait(false);
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      // Named decision (CA1031): one child's supervision failure must never take down
      // the tick guarding the others.
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        await AppendAsync(new WatchdogEvent(Guid.NewGuid(), supervisor.ChildId,
            WatchdogEventKind.WatchdogErrored, ex.Message, 0, null,
            services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
      }
    }
  }

  /// <summary>Bounded settle observation via the runtime's WhenSettledAsync: an AWAIT
  ///     (not a poll) with a hard iteration bound on the injected clock so a frozen
  ///     TimeProvider cannot hang the tick. Unknown ids settle as Running (no-op).</summary>
  private async Task<AgentRecord> AwaitSettledAsync(AgentId id, CancellationToken ct)
  {
    Result<AgentRecord> loaded = await services.Store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      // Unknown id: nothing to observe; the caller's guard treats it as not settled.
      return new AgentRecord(id, null, 0, AgentStatus.Running, null, "unknown", null, "", services.Clock.GetUtcNow(), null, null);
    }

    AgentRecord record = loaded.Value;
    if (record.Status is not AgentStatus.Running)
    {
      return record;
    }

    Result<AgentRunOutcome> outcome = await services.Runtime.WhenSettledAsync(id, ct)
        .WaitAsync(services.Policy.SettleWait, ct).ConfigureAwait(false);
    if (!outcome.IsSuccess)
    {
      return record; // timeout or unknown: still Running from the tick's point of view
    }

    Result<AgentRecord> settled = await services.Store.GetAsync(id, ct).ConfigureAwait(false);
    return settled.IsSuccess ? settled.Value : record;
  }
  /// <summary>Same-id retry: reset the record, forget the heartbeat slate, start.</summary>
  private async Task RetryAsync(AgentRecord settled, int nextAttempt, CancellationToken ct)
  {
    AgentRecord reset = settled with
    {
      Status = AgentStatus.Running,
      FailureReason = null,
      CompletedAt = null,
      FinalReport = null,
      Attempts = nextAttempt,
      Phase = ChildPhase.ModelCall,
    };
    Result<string> updated = await services.Store.UpdateAsync(reset, ct).ConfigureAwait(false);
    if (!updated.IsSuccess)
    {
      return;
    }

    services.Heartbeat.Forget(reset.Id);
    Result<AgentId> started = await services.Runtime.Start(reset, ct).ConfigureAwait(false);
    if (started.IsSuccess)
    {
      await AppendAsync(new WatchdogEvent(Guid.NewGuid(), reset.Id,
          WatchdogEventKind.RetrySpawned, "wrap-up retry started on the same id", nextAttempt, null,
          services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
    }
  }
  private async Task SweepOneAsync(AgentRecord record, CancellationToken ct)
  {
    if (record.Status is not AgentStatus.Running)
    {
      return;
    }

    // Heartbeat-presence gate: no entry means another container's run, or a run that
    // has not beaten yet. Never act on a row this process cannot actually cancel.
    if (!services.Heartbeat.TryGetLastBeat(record.Id, out DateTimeOffset beat))
    {
      return;
    }

    TimeSpan idleAge = services.Clock.GetUtcNow() - beat;
    Result<int> attempts = await services.Events.CountKindForAgentAsync(
        record.Id, WatchdogEventKind.RetrySpawned, ct).ConfigureAwait(false);
    if (!attempts.IsSuccess)
    {
      return; // cannot read the ledger; refuse to act on a guess
    }

    Result<int> deferred = await services.Events.CountKindForAgentAsync(
        record.Id, WatchdogEventKind.RetryDeferred, ct).ConfigureAwait(false);
    if (!deferred.IsSuccess)
    {
      return; // cannot read the ledger; refuse to act on a guess
    }

    // A deferred retry means the previous cancel was never observed: per the spec the
    // NEXT detection takes the terminal path regardless of the RetrySpawned count.
    WatchdogPolicyDecision decision = deferred.Value > 0
        ? WatchdogPolicyDecision.TerminalReport
        : services.Policy.Decide(record.ParentId is not null, idleAge, attempts.Value);
    if (decision == WatchdogPolicyDecision.RetryWrapUp)
    {
      await RetryWrapUpAsync(record, attempts.Value, idleAge, ct).ConfigureAwait(false);
    }
    else if (decision == WatchdogPolicyDecision.TerminalReport)
    {
      await TerminalAsync(record, attempts.Value, idleAge, ct).ConfigureAwait(false);
    }
  }

  private async Task RetryWrapUpAsync(AgentRecord record, int attempts, TimeSpan idleAge, CancellationToken ct)
  {
    await AppendAsync(HungDetectedEvent(record, idleAge), ct).ConfigureAwait(false);
    services.Runtime.Interrupt(record.Id);
    AgentRecord settled = await AwaitSettleAsync(record.Id, ct).ConfigureAwait(false);
    if (settled.Status is AgentStatus.Running)
    {
      // Cancel never observed (e.g. a native block): starting a second concurrent run on
      // the same id would break the single-writer invariant. Defer; next tick escalates.
      await AppendAsync(new WatchdogEvent(Guid.NewGuid(), record.Id,
          WatchdogEventKind.RetryDeferred,
"cancel not observed within settle wait; deferring", attempts, null,
          services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
      return;
    }

    if (settled.Status is AgentStatus.Completed)
    {
      return; // finished while we watched - nothing to retry
    }

    AgentRecord reset = settled with
    {
      Status = AgentStatus.Running,
      FailureReason = null,
      CompletedAt = null,
      FinalReport = null,
    };
    Result<string> updated = await services.Store.UpdateAsync(reset, ct).ConfigureAwait(false);
    if (!updated.IsSuccess)
    {
      return; // the event trail carries the detection; the next tick re-evaluates
    }

    services.Heartbeat.Forget(record.Id); // fresh run starts with a clean liveness slate
    Result<AgentId> started = await services.Runtime.Start(reset, ct).ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      return; // no retry actually began; next tick re-evaluates with the ledger intact
    }
    await AppendAsync(new WatchdogEvent(Guid.NewGuid(), record.Id,
        WatchdogEventKind.RetrySpawned,
        "wrap-up retry started on the same id", attempts + 1, null,
        services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
  }

  private async Task TerminalAsync(AgentRecord record, int attempts, TimeSpan idleAge, CancellationToken ct)
  {
    await AppendAsync(HungDetectedEvent(record, idleAge), ct).ConfigureAwait(false);
    services.Runtime.Interrupt(record.Id);
    AgentRecord settled = await AwaitSettleAsync(record.Id, ct).ConfigureAwait(false);
    if (settled.Status is AgentStatus.Running or AgentStatus.Completed)
    {
      return; // cannot confirm a hang we could not cancel, or it just finished
    }

    Result<string> marked = await services.Store.UpdateAsync(settled with
    {
      Status = AgentStatus.Failed,
      FailureReason = AgentFailureReason.Hung,
      CompletedAt = services.Clock.GetUtcNow(),
      FinalReport = "Error [Hung]: no activity for " + (int)idleAge.TotalMinutes
          + " minutes; terminated after wrap-up retry.",
    }, ct).ConfigureAwait(false);
    if (!marked.IsSuccess)
    {
      return;
    }

    await AppendAsync(new WatchdogEvent(Guid.NewGuid(), record.Id,
        WatchdogEventKind.TerminalReport, "marked Failed(Hung)", attempts, null,
        services.Clock.GetUtcNow()), ct).ConfigureAwait(false);
  }

  /// <summary>Iteration-bounded settle poll: at most one poll per interval for SettleWait's
  ///     worth of polls, so even a frozen TimeProvider cannot hang the tick.</summary>
  private async Task<AgentRecord> AwaitSettleAsync(AgentId id, CancellationToken ct)
  {
    int maxPolls = Math.Max(1, (int)services.Policy.SettleWait.TotalSeconds);
    for (int poll = 0; poll < maxPolls; poll++)
    {
      Result<AgentRecord> loaded = await services.Store.GetAsync(id, ct).ConfigureAwait(false);
      if (loaded.IsSuccess && loaded.Value.Status is not AgentStatus.Running)
      {
        return loaded.Value;
      }

      await Task.Delay(_settlePollInterval, ct).ConfigureAwait(false);
    }

    Result<AgentRecord> final = await services.Store.GetAsync(id, ct).ConfigureAwait(false);
    return final.IsSuccess
        ? final.Value
        : throw new InvalidOperationException("settle poll lost the record");
  }

  private WatchdogEvent HungDetectedEvent(AgentRecord record, TimeSpan idleAge)
      => new(Guid.NewGuid(), record.Id, WatchdogEventKind.HungDetected,
          "no heartbeat for " + (int)idleAge.TotalMinutes + " minutes", 0, null,
          services.Clock.GetUtcNow());

  private async Task SampleRssAsync(CancellationToken ct)
  {
    double megabytes = services.Metrics.WorkingSetBytes() / (1024.0 * 1024.0);
    if (megabytes < services.Options.RssThresholdMb)
    {
      _rssBreachSince = null;
      return;
    }

    DateTimeOffset now = services.Clock.GetUtcNow();
    _rssBreachSince ??= now;
    if (_lastRssReport is { } last && now - last < services.Options.RssReReportInterval)
    {
      return;
    }

    _lastRssReport = now;
    await AppendAsync(new WatchdogEvent(Guid.NewGuid(), null, WatchdogEventKind.RssBreached,
        "working set " + megabytes.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
            + " MB exceeds threshold " + services.Options.RssThresholdMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        0, Math.Round(megabytes, 1), now), ct).ConfigureAwait(false);
  }

  private async Task RecordErrorAsync(string message, CancellationToken ct)
  {
    DateTimeOffset now = services.Clock.GetUtcNow();
    if (_lastErrorReport is { } last && now - last < TimeSpan.FromMinutes(10))
    {
      return;
    }

    _lastErrorReport = now;
    await AppendAsync(new WatchdogEvent(Guid.NewGuid(), null,
        WatchdogEventKind.WatchdogErrored, message, 0, null, now), ct).ConfigureAwait(false);
  }

  /// <summary>Best-effort by contract: a failed event write never blocks enactment.</summary>
  private async Task AppendAsync(WatchdogEvent evt, CancellationToken ct)
  {
    Result<string> ignored = await services.Events.AppendAsync(evt, ct).ConfigureAwait(false);
    _ = ignored;
  }

  /// <summary>Releases the event subscription: a detached watchdog stops observing its
  ///     container's stream.</summary>
  public void Dispose() => _feedLease?.Dispose();

}

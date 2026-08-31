using eThangAgent.ModelDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Per-child supervision machinery (D6): the idle timer fed by heartbeat beats,
///     budget accumulators fed by per-call usage reports, the attempt count and phase as
///     owned facts. One instance per RUNNING child; timers die with the child —
///     O(running), no central sweep (P4). Raises ChildIdleAlertEvent when CheckIdle
///     observes the threshold crossed; budget alerts fire from OnUsage at soft thresholds.
///     Every time read goes through the injected TimeProvider.</summary>
public sealed class ChildSupervisor(AgentId childId, IAgentEvents events, TimeProvider clock, BudgetCeilings? ceilings)
{
  /// <summary>Soft threshold as a fraction of a ceiling; the graduated response (nudge,
  ///     retry, terminal) stays watchdog policy — the supervisor only raises facts (P5).</summary>
  public const double DefaultSoftThreshold = 0.8;

  private readonly Lock _gate = new();
  private DateTimeOffset _lastBeat;
  private ChildPhase _phase = ChildPhase.ModelCall;
  private long _tokens;
  private bool _idleAlerted;
  private readonly HashSet<string> _budgetAlerts = [];

  public AgentId ChildId { get; } = childId;
  public int Attempts { get; private set; }

  /// <summary>Records a run (re)start: the attempt count is owned here as fact (FR-L2),
  ///     mirroring the record column the runtime writes at the same transition.</summary>
  public void OnStart(int attempts)
  {
    lock (_gate)
    {
      Attempts = attempts;
      _lastBeat = clock.GetUtcNow();
      _idleAlerted = false;
    }
  }

  /// <summary>Heartbeat push; resets the idle window.</summary>
  public void OnBeat()
  {
    lock (_gate)
    {
      _lastBeat = clock.GetUtcNow();
      _idleAlerted = false;
    }
  }

  /// <summary>Phase fact from progress events.</summary>
  public void OnPhase(ChildPhase phase)
  {
    lock (_gate)
    {
      _phase = phase;
    }
  }

  /// <summary>Accumulates one provider call's usage and raises a ChildBudgetAlertEvent
  ///     the first time a non-null ceiling's soft threshold is crossed (D8: resources,
  ///     never time). Hard-ceiling enforcement is a policy decision enacted elsewhere.</summary>
  public void OnUsage(TokenUsage usage)
  {
    ChildBudgetAlertEvent? alert = null;
    lock (_gate)
    {
      _tokens += usage.InputTokens + usage.OutputTokens;
      if (ceilings?.MaxTokens is { } tokenCeiling && _tokens >= tokenCeiling * DefaultSoftThreshold
          && _budgetAlerts.Add($"tokens:{tokenCeiling}"))
      {
        alert = new ChildBudgetAlertEvent(ChildId, clock.GetUtcNow(), "tokens", _tokens,
            tokenCeiling, BurnRatePerMinute());
      }
    }

    if (alert is not null)
    {
      events.Publish(alert);
    }
  }

  /// <summary>Idle observation for the watchdog tick. Returns the alert exactly once per
  ///     idle episode; a subsequent beat re-arms it.</summary>
  public ChildIdleAlertEvent? CheckIdle(TimeSpan idleThreshold)
  {
    lock (_gate)
    {
      TimeSpan idle = clock.GetUtcNow() - _lastBeat;
      if (idle < idleThreshold || _idleAlerted)
      {
        return null;
      }

      _idleAlerted = true;
      ChildIdleAlertEvent alert = new(ChildId, clock.GetUtcNow(), idle, _phase.ToString());
      events.Publish(alert);
      return alert;
    }
  }
  /// <summary>Facts for an idle alert's burn-rate field: tokens per minute of run time.
  ///     Near-zero burn while idle alerts fire is strong stuck evidence (FR-B5).</summary>
  private double BurnRatePerMinute()
  {
    double minutes = Math.Max((clock.GetUtcNow() - _lastBeat).TotalMinutes, 0.01);
    return _tokens / minutes;
  }
}

/// <summary>Thread-safe per-session registry of running children's supervisors. The
///     runtime registers at Start and unregisters at settle; the watchdog tick iterates
///     instead of sweeping the agent store (P4).</summary>
public sealed class ChildSupervisorRegistry
{
  private readonly Lock _gate = new();
  private readonly Dictionary<Guid, ChildSupervisor> _supervisors = [];

  public void Register(AgentId id, ChildSupervisor supervisor)
  {
    lock (_gate)
    {
      _supervisors[id.Value] = supervisor;
    }
  }

  public void Unregister(AgentId id)
  {
    lock (_gate)
    {
      _ = _supervisors.Remove(id.Value);
    }
  }

  public IReadOnlyList<ChildSupervisor> All
  {
    get
    {
      lock (_gate)
      {
        return [.. _supervisors.Values];
      }
    }
  }
}

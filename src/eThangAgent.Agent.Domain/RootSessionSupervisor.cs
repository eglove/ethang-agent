namespace eThangAgent.AgentDomain;

/// <summary>Liveness and idle tracking for ONE ROOT session (issue #301 D5).
///     <para>WHY THIS IS NEW MACHINERY AND NOT A REUSE. Nothing today mints a supervisor for a
///     root: <c>ChildSupervisor</c>s are created per CHILD RUN inside the runtime's
///     <c>BeginRun</c>, the watchdog sweeps <c>DescendantsOf(rootId)</c> whose walk starts from
///     a record's <c>ParentId</c> so the root is never in the set, and orphan repair exempts the
///     root explicitly because it is Running by design between turns. The policy is reused;
///     the supervision is built.</para>
///     <para>THE CLOCK IS RE-SOURCED. Its input is <see cref="RootTurnActivity"/> - facts the
///     ROOT loop produces (turn started, tool started, streamed, turn settled) - and NOT
///     <see cref="ChildProgressEvent"/>, which describes a child run and is retired by #305/#316.
///     A clock fed by a retired event would stop ticking silently: the root would never read as
///     idle and would never be nudged.</para>
///     <para>THREAD-SAFE. Facts arrive from the host's event pump while the tick reads them, and
///     the idle alert is raised under the same lock so it can be handled exactly once per
///     breach - the <c>ChildSupervisor.CheckIdle</c> pattern, for the same reason.</para></summary>
public sealed class RootSessionSupervisor(AgentId sessionId)
{
  private readonly Lock _gate = new();
  private DateTimeOffset? _lastActivity;
  private RootTurnPhase _phase = RootTurnPhase.TurnSettled;
  private string _label = "never started";
  private int _wrapUpAttempts;
  private bool _idleAlertRaised;

  /// <summary>The root session this supervisor watches.</summary>
  public AgentId SessionId { get; } = sessionId;

  /// <summary>How many wrap-up attempts have already been spent on this session.</summary>
  public int WrapUpAttempts
  {
    get
    {
      lock (_gate)
      {
        return _wrapUpAttempts;
      }
    }
  }

  /// <summary>The phase of the most recent fact, for a human-readable alert.</summary>
  public RootTurnPhase Phase
  {
    get
    {
      lock (_gate)
      {
        return _phase;
      }
    }
  }

  /// <summary>Records one root-turn fact. A WORK fact resets the idle clock and clears the
  ///     raised alert, so the next breach is a new one; a SETTLED fact starts the clock
  ///     without clearing an alert the tick has not handled yet.</summary>
  public void OnActivity(RootTurnActivity activity)
  {
    ArgumentNullException.ThrowIfNull(activity);
    lock (_gate)
    {
      _lastActivity = activity.At;
      _phase = activity.Phase;
      _label = activity.Label;
      if (activity.IsWork)
      {
        _idleAlertRaised = false;
      }
    }
  }

  /// <summary>Records that a wrap-up attempt was spent, so the next breach escalates.
  ///     Called by the enactment AFTER it nudged, never before: a nudge that never landed
  ///     must not consume the budget.</summary>
  public void RecordWrapUp()
  {
    lock (_gate)
    {
      _wrapUpAttempts++;
    }
  }

  /// <summary>The idle age, or null when the session has produced no fact yet (a session
  ///     that never started is not idle - it is not running, and nudging it would be a
  ///     message to nobody).</summary>
  public TimeSpan? IdleAge(DateTimeOffset now)
  {
    lock (_gate)
    {
      return _lastActivity is { } last ? now - last : null;
    }
  }

  /// <summary>Raises the idle alert when the threshold is crossed, ONCE per breach: a tick
  ///     that re-raised every interval would spend the whole wrap-up budget on one stall.
  ///     Returns null when the session is not idle, or when this breach was already
  ///     raised.</summary>
  public RootIdleAlert? CheckIdle(TimeSpan threshold, DateTimeOffset now)
  {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
    lock (_gate)
    {
      if (_lastActivity is not { } last || _idleAlertRaised)
      {
        return null;
      }

      TimeSpan age = now - last;
      if (age < threshold)
      {
        return null;
      }

      _idleAlertRaised = true;
      return new RootIdleAlert(SessionId, now, age, _phase, _label);
    }
  }
}

/// <summary>The supervisor's idle alert: the root session, how long it has been quiet, and
///     the phase it went quiet in. Metadata only.</summary>
public sealed record RootIdleAlert(AgentId SessionId, DateTimeOffset At, TimeSpan IdleAge,
    RootTurnPhase Phase, string Label);

namespace eThangAgent.AgentDomain;

/// <summary>Why a waiting agent should wake. UserMessage is the unconditional
/// user-steering wake; the rest map 1:1 onto the child-event family plus state writes.</summary>
public enum WakeReason
{
  UserMessage,
  Idle,
  StreamStall,
  StateWrite,
  Budget,
  Mailbox,

  /// <summary>A durable PLAN record changed (created, step, or status). Distinct from
  ///     <see cref="StateWrite"/> because plan work is the progress signal a parent agent
  ///     actually cares about: a child marking a step done is the fact a waiting parent
  ///     should wake on, and it is not a state-key write.</summary>
  Plan,
}

/// <summary>A wake event. <paramref name="DigestLine"/> is the one-line digest entry rendered
/// into the wait result — metadata only (D5): ids, phases, durations, never message or report content.</summary>
public sealed record WakeSignal(WakeReason Reason, AgentId ChildId, DateTimeOffset At, string DigestLine);

/// <summary>The seam the wait logic depends on. Subscriptions are leases: registering a signal
/// wakes current subscribers only — no replay.</summary>
public interface IWaitWakeSource
{
  IDisposable Subscribe(Action<WakeSignal> observer);
}

/// <summary>Per-session wake multiplexer. Fan-out in subscription order, at-most-once, ephemeral.
/// Subscriber faults are contained at the hub (the same fault-boundary doctrine as InProcessAgentEvents).
/// Thread-safe: signals may be registered from any thread.</summary>
public sealed class WaitWakeHub(Action<string>? faultLog = null) : IWaitWakeSource
{
  private readonly Lock _gate = new();
  private readonly List<Action<WakeSignal>> _observers = [];
  // Sticky signals by reason: a pending user-steering fact persists until the inbox
  // clears it, so a wait that subscribes LATER still wakes for it.
  private readonly Dictionary<WakeReason, WakeSignal> _sticky = [];
  private readonly Action<string>? _faultLog = faultLog;

  public IDisposable Subscribe(Action<WakeSignal> observer)
  {
    ArgumentNullException.ThrowIfNull(observer);
    WakeSignal[] pending;
    lock (_gate)
    {
      _observers.Add(observer);
      pending = [.. _sticky.Values];
    }

    // Sticky delivery AFTER releasing the gate: a pending user message wakes the
    // wait that subscribes while the message is still undelivered.
    foreach (WakeSignal signal in pending)
    {
      try
      {
        observer(signal);
      }
#pragma warning disable CA1031 // Do not catch general exception types
      // Named decision: the hub is a fault boundary — see the class summary.
      catch (Exception ex)
      {
        _faultLog?.Invoke($"wait-wake subscriber fault: {ex.Message}");
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    return new Lease(this, observer);
  }

  public void Register(WakeSignal signal)
  {
    ArgumentNullException.ThrowIfNull(signal);
    Action<WakeSignal>[] snapshot;
    lock (_gate)
    {
      snapshot = [.. _observers];
    }

    Dispatch(signal, snapshot);
  }

  /// <summary>Registers a STICKY wake: delivered to current subscribers immediately AND
  ///     to every subscriber that subscribes while it remains set — the pending-fact
  ///     semantics the user-steering wake needs. The user message that lands while the
  ///     parent is between safe points stays pending in the inbox; the wait that starts
  ///     LATER must still wake for it (the ephemeral no-replay contract loses exactly
  ///     that signal). ClearSticky stops further delivery — the inbox clears it when
  ///     the message is actually delivered.</summary>
  public void RegisterSticky(WakeSignal signal)
  {
    ArgumentNullException.ThrowIfNull(signal);
    lock (_gate)
    {
      _sticky[signal.Reason] = signal;
    }

    Action<WakeSignal>[] stickySnapshot;
    lock (_gate)
    {
      stickySnapshot = [.. _observers];
    }

    Dispatch(signal, stickySnapshot);
  }

  /// <summary>Clears the sticky signal for one reason: later subscribers no longer
  ///     receive it. Idempotent.</summary>
  public void ClearSticky(WakeReason reason)
  {
    lock (_gate)
    {
      _ = _sticky.Remove(reason);
    }
  }

  /// <summary>Delivers one signal to a subscriber snapshot with fault containment:
  ///     one throwing subscriber never prevents the remaining subscribers from being
  ///     woken; faults surface through the optional fault log instead of escaping.</summary>
  private void Dispatch(WakeSignal signal, Action<WakeSignal>[] snapshot)
  {
    foreach (Action<WakeSignal> observer in snapshot)
    {
      try
      {
        observer(signal);
      }
#pragma warning disable CA1031 // Do not catch general exception types
      // Named decision: the hub is a fault boundary — see the class summary.
      catch (Exception ex)
      {
        _faultLog?.Invoke($"wait-wake subscriber fault: {ex.Message}");
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }
  }

  private sealed class Lease(WaitWakeHub owner, Action<WakeSignal> observer) : IDisposable
  {
    public void Dispose()
    {
      lock (owner._gate)
      {
        _ = owner._observers.Remove(observer);
      }
    }
  }
}
/// <summary>The no-op wake source: subscribes nobody, publishes nothing. Used when
///     no hub is wired but a timeout was requested — the wait then races settlement
///     against the timeout alone.</summary>
public sealed class NullWaitWakeSource : IWaitWakeSource
{
  public static readonly NullWaitWakeSource Instance = new();

  public IDisposable Subscribe(Action<WakeSignal> observer) => new NoLease();

  private sealed class NoLease : IDisposable
  {
    public void Dispose()
    {
    }
  }
}

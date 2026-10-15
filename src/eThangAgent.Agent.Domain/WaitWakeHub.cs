namespace eThangAgent.AgentDomain;

/// <summary>Why a waiting agent should wake. UserMessage is the unconditional
/// user-steering wake; the rest map 1:1 onto the child-event family plus state writes.</summary>
public enum WakeReason
{
  UserMessage,
  Progress,
  Idle,
  StreamStall,
  StateWrite,
  Budget,
  Mailbox,
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
  private readonly Action<string>? _faultLog = faultLog;

  public IDisposable Subscribe(Action<WakeSignal> observer)
  {
    ArgumentNullException.ThrowIfNull(observer);
    lock (_gate)
    {
      _observers.Add(observer);
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

    foreach (Action<WakeSignal> observer in snapshot)
    {
      try
      {
        observer(signal);
      }
#pragma warning disable CA1031 // Do not catch general exception types
      // Named decision: the hub is a fault boundary — one throwing subscriber must
      // never prevent the remaining subscribers from being woken. Faults are surfaced
      // through the optional fault log instead of escaping.
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

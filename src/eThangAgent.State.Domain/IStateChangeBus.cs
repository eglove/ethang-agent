namespace eThangAgent.StateDomain;

public enum StateChangeKind
{
  Set,
  Append,
  Delete,
}

/// <summary>One durable-state mutation. Metadata only — key names and kinds, never
///     values (the same metadata-only doctrine the child-event family applies).</summary>
public sealed record StateChange(string WorkspaceId, string Key, StateChangeKind Kind, string? WriterLabel, DateTimeOffset At);

/// <summary>In-process, ephemeral stream of state mutations (no replay, no persistence).
///     The Agent Domain adapts this onto its wake hub at composition; the State Domain
///     must not reference the Agent Domain.</summary>
public interface IStateChangeBus
{
  IDisposable Subscribe(Action<StateChange> observer);

  void Publish(StateChange change);
}

/// <summary>Fan-out implementation: subscription order, at-most-once, ephemeral,
///     subscriber faults contained and logged (the WaitWakeHub/InProcessAgentEvents
///     doctrine). Thread-safe.</summary>
public sealed class InProcessStateChangeBus(Action<string>? faultLog = null) : IStateChangeBus
{
  private readonly Lock _gate = new();
  private readonly List<Action<StateChange>> _observers = [];
  private readonly Action<string>? _faultLog = faultLog;

  public IDisposable Subscribe(Action<StateChange> observer)
  {
    ArgumentNullException.ThrowIfNull(observer);
    lock (_gate)
    {
      _observers.Add(observer);
    }

    return new Lease(this, observer);
  }

  public void Publish(StateChange change)
  {
    ArgumentNullException.ThrowIfNull(change);
    Action<StateChange>[] snapshot;
    lock (_gate)
    {
      snapshot = [.. _observers];
    }

    foreach (Action<StateChange> observer in snapshot)
    {
      // Named decision (CA1031): the stream is a fault boundary — one subscriber's
      // failure must never reach the publishing write nor the other subscribers.
      try
      {
        observer(change);
      }
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
      {
        _faultLog?.Invoke($"state-change subscriber fault: {ex.Message}");
      }
#pragma warning restore CA1031
    }
  }

  private sealed class Lease(InProcessStateChangeBus owner, Action<StateChange> observer) : IDisposable
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

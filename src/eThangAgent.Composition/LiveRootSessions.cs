using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>The root sessions this process has open, so a session open can tell a live session
///     from a dead one.
///     <para>LIVENESS IS A PROPERTY OF THE PROCESS, never of the row. A persisted Running record
///     says a run was started, not that anything is running it now, so repair has to ask
///     something only a live process can answer.</para>
///     <para>ONE INSTANCE PER FACTORY, and a factory is one process. A session registers when
///     its root id is known and releases when its container is disposed, so the set holds
///     exactly the sessions this process has open.</para>
///     <para>COUNTED, NOT A SET: a resume builds a new container for an id the previous
///     container may still be releasing, and a plain set would let the older release erase the
///     newer registration - reporting a live session dead for the rest of the process's life.
///     </para></summary>
public sealed class LiveRootSessions
{
  private readonly Lock _gate = new();
  private readonly Dictionary<Guid, int> _live = [];

  /// <summary>Records <paramref name="id"/> as live until the returned lease is disposed.
  ///     Disposing twice is a no-op, so a container disposed by two callers releases once.
  ///     </summary>
  /// <param name="id">The root session id.</param>
  public IDisposable Register(AgentId id)
  {
    lock (_gate)
    {
      _ = _live.TryGetValue(id.Value, out int held);
      _live[id.Value] = held + 1;
    }

    return new Lease(this, id.Value);
  }

  /// <summary>The ids live now.</summary>
  public IReadOnlyCollection<Guid> Snapshot()
  {
    lock (_gate)
    {
      return [.. _live.Keys];
    }
  }

  /// <summary>Drops one hold on <paramref name="id"/>, forgetting it once none is left.</summary>
  /// <param name="id">The root session id.</param>
  private void Release(Guid id)
  {
    lock (_gate)
    {
      if (!_live.TryGetValue(id, out int held))
      {
        return;
      }

      if (held <= 1)
      {
        _ = _live.Remove(id);
        return;
      }

      _live[id] = held - 1;
    }
  }

  /// <summary>One hold on one id, released at most once.</summary>
  /// <param name="owner">The registry the hold belongs to.</param>
  /// <param name="id">The root session id.</param>
  private sealed class Lease(LiveRootSessions owner, Guid id) : IDisposable
  {
    private int _released;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _released, 1) == 0)
      {
        owner.Release(id);
      }
    }
  }
}

/// <summary>Holds one session's liveness lease inside its own container, so the lease is
///     released by the disposal that already tears the session down.
///     <para>A CARRIER RATHER THAN A CONSTRUCTOR ARGUMENT: the root id is minted after the
///     container is built, exactly as the session-file and chain-contract carriers are, so the
///     hold is filled at the one moment the id exists.</para></summary>
/// <param name="registry">The process-wide registry.</param>
public sealed class LiveSessionCarrier(LiveRootSessions registry) : IDisposable
{
  private IDisposable? _lease;

  /// <summary>Registers <paramref name="id"/> as live for this container's lifetime.</summary>
  /// <param name="id">The root session id, once it is known.</param>
  public void Fill(AgentId id) => _lease = registry.Register(id);

  /// <summary>Releases the hold. Called by the container's own disposal.</summary>
  public void Dispose() => _lease?.Dispose();
}

namespace eThangAgent.ComputerUse.Host;

/// <summary>One broker's controller lease (task 16): the first authenticated connection
///     to claim control owns it; every other claimant is refused with controller_busy
///     (details.owner carries the owning connection id). Release frees the lease; the
///     lease auto-expires when the owning connection drops - the connection layer calls
///     <see cref="Release"/> on pipe EOF, which raises <see cref="OwnerLost"/>. A3:
///     the input layer subscribes to OwnerLost to cancel ACTIVE key holds and clear the
///     synthetic modifier state immediately (the callback is the contract).</summary>
internal sealed class ControllerLease
{
  private readonly Lock _gate = new();
  private int? _owner;

  /// <summary>Raised exactly once per owner loss (release, explicit or disconnect),
  ///     carrying the connection id that lost control. Exceptions from subscribers are
  ///     isolated: one failing handler must not skip the others.</summary>
  public event Action<int>? OwnerLost;

  /// <summary>The current owner's connection id, or null when control is free.</summary>
  public int? Owner
  {
    get
    {
      _gate.Enter();
      try
      {
        return _owner;
      }
      finally
      {
        _gate.Exit();
      }
    }
  }

  /// <summary>Attempts to claim control: true for the winner; a refusal carrying the
  ///     current owner for the controller_busy details otherwise.</summary>
  public LeaseAcquireResult TryAcquire(int connectionId)
  {
    _gate.Enter();
    try
    {
      if (_owner is { } current)
      {
        return LeaseAcquireResult.Busy(current);
      }

      _owner = connectionId;
      return LeaseAcquireResult.Granted();
    }
    finally
    {
      _gate.Exit();
    }
  }

  /// <summary>Releases control (owner or broker on connection drop). Returns false when
  ///     the caller is not the owner; the lease then stays with its owner. Owner loss
  ///     raises <see cref="OwnerLost"/> outside the lock.</summary>
  public bool Release(int connectionId)
  {
    bool lost;
    _gate.Enter();
    try
    {
      if (_owner != connectionId)
      {
        return false;
      }

      _owner = null;
      lost = true;
    }
    finally
    {
      _gate.Exit();
    }

    if (lost)
    {
      RaiseOwnerLost(connectionId);
    }

    return true;
  }

  private void RaiseOwnerLost(int connectionId)
  {
    Action<int>? handlers = OwnerLost;
    if (handlers is null)
    {
      return;
    }

    // Subscribers run inline: the release path is the A3 fence (holds must be
    // cancelled BEFORE anything else can start), and a fault in one handler must
    // never skip the others. The explicit loop keeps both properties.
    foreach (Action<int> handler in handlers.GetInvocationList().Cast<Action<int>>())
    {
      try
      {
        handler(connectionId);
      }
#pragma warning disable CA1031 // Named decision: a failing subscriber must not skip the rest nor corrupt lease state; the boundary is the notification fan-out.
      catch (Exception)
      {
        // Swallowed by contract: isolation, not silence - the remaining handlers still run.
      }
#pragma warning restore CA1031
    }
  }
};

/// <summary>The outcome of a lease claim: granted, or busy with the owning connection.</summary>
internal readonly record struct LeaseAcquireResult(bool Acquired, int? Owner)
{
  public static LeaseAcquireResult Granted() => new(true, null);
  public static LeaseAcquireResult Busy(int owner) => new(false, owner);
};

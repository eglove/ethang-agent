namespace eThangAgent.AgentDomain;

/// <summary>Admission at the HOST boundary (issue #305 criterion 4): the cap counts live hosts,
///     and a start past the cap PARKS instead of dropping. Parking is the load-bearing half —
///     the issue's words are 'a persisted, resumable park — not a drop', because a dropped
///     spawn is a row marked Running with no run behind it, which is exactly the stranded-row
///     defect (#305 D5) the same issue fixes.
///     <para>PURE BY CONSTRUCTION. This type holds the arithmetic and the ordering rule only:
///     no clock, no store, no process. The owner enacts the decision (persisting a parked row,
///     releasing a slot to the queue head), so the policy is testable without a live host and
///     cannot drift into a second implementation of the cap.</para></summary>
public sealed class HostAdmissionGate
{
  private readonly Lock _gate = new();
  private readonly List<ParkedStart> _parked = [];
  private int _live;

  /// <summary>Creates a gate bounded by <paramref name="capacity"/>. A capacity below
  ///     <see cref="HostCapacity.MinimumHosts"/> is refused rather than clamped: a zero cap
  ///     would park every start forever, and a silent clamp would hide the caller's bug.</summary>
  public HostAdmissionGate(int capacity)
  {
    if (capacity < HostCapacity.MinimumHosts)
    {
      throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
          "the host capacity must admit at least one host.");
    }

    Capacity = capacity;
  }

  /// <summary>The cap this gate enforces.</summary>
  public int Capacity { get; }

  /// <summary>Hosts currently holding a slot.</summary>
  public int Live
  {
    get
    {
      lock (_gate)
      {
        return _live;
      }
    }
  }

  /// <summary>Starts currently parked, in admission order.</summary>
  public int ParkedCount
  {
    get
    {
      lock (_gate)
      {
        return _parked.Count;
      }
    }
  }

  /// <summary>Asks for a slot for <paramref name="recordId"/>. Admitted when a slot is free;
  ///     otherwise PARKED, and the caller persists the park so the start is resumable and
  ///     `agent.status` can report it honestly. Admission is first-come, first-served: a
  ///     priority-then-FIFO order belongs to whoever knows the priorities, and this gate is
  ///     deliberately the simpler contract.</summary>
  public HostAdmission Request(Guid recordId)
  {
    lock (_gate)
    {
      if (_live < Capacity)
      {
        _live++;
        return HostAdmission.Admitted(recordId);
      }

      _parked.Add(new ParkedStart(recordId, _parked.Count));
      return HostAdmission.Parked(recordId, _parked.Count);
    }
  }

  /// <summary>Releases one slot and hands it DIRECTLY to the head of the park queue inside the
  ///     same critical section, so a fresh start can neither starve the queue nor push the live
  ///     count past the cap (the same admission rule #238 established for slots, applied to
  ///     hosts). Returns the newly admitted start, or null when nothing was parked — in which
  ///     case the live count simply drops.</summary>
  public HostAdmission? Release()
  {
    lock (_gate)
    {
      if (_parked.Count == 0)
      {
        _live = Math.Max(0, _live - 1);
        return null;
      }

      // The slot transfers: _live is decremented then re-incremented in one critical
      // section, so the count never dips below what is actually running.
      ParkedStart head = _parked[0];
      _parked.RemoveAt(0);
      return HostAdmission.Admitted(head.RecordId);
    }
  }

  /// <summary>Withdraws a parked start (the caller cancelled or interrupted it). Returns true
  ///     when it was parked and is now gone. A withdrawn start releases no slot — it never
  ///     held one — so the queue does not advance.</summary>
  public bool Withdraw(Guid recordId)
  {
    lock (_gate)
    {
      int index = _parked.FindIndex(p => p.RecordId == recordId);
      if (index < 0)
      {
        return false;
      }

      _parked.RemoveAt(index);
      return true;
    }
  }

  private sealed record ParkedStart(Guid RecordId, int Position);
}

/// <summary>One admission decision: the record, whether it may run now, and its position when
///     parked (0 when admitted).</summary>
public sealed record HostAdmission(Guid RecordId, bool IsAdmitted, int ParkPosition)
{
  public static HostAdmission Admitted(Guid recordId) => new(recordId, true, 0);

  public static HostAdmission Parked(Guid recordId, int position) => new(recordId, false, position);
}

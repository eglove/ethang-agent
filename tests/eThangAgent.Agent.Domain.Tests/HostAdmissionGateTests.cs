using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>Admission at the host boundary (issue #305 criterion 4). The criterion is specific:
///     the behaviour past the cap must be a PERSISTED, RESUMABLE PARK — not a drop. A dropped
///     start is a row marked Running with no run behind it, which is the stranded-row defect
///     (#305 D5) the same issue fixes, so parking is load-bearing rather than a nicety.</summary>
public class HostAdmissionGateTests
{
  [Fact]
  public void Request_UnderCapacity_IsAdmitted()
  {
    HostAdmissionGate gate = new(capacity: 2);

    Assert.True(gate.Request(Guid.NewGuid()).IsAdmitted);
    Assert.Equal(1, gate.Live);
  }

  [Fact]
  public void Request_PastCapacity_IsParked_NotDropped()
  {
    // The criterion, stated directly: past the cap the start PARKS. It is not refused and not
    // silently discarded, because either would leave a row no run will ever claim.
    HostAdmissionGate gate = new(capacity: 1);
    _ = gate.Request(Guid.NewGuid());
    Guid second = Guid.NewGuid();

    HostAdmission decision = gate.Request(second);

    Assert.False(decision.IsAdmitted);
    Assert.Equal(second, decision.RecordId);
    Assert.Equal(1, decision.ParkPosition);
    Assert.Equal(1, gate.ParkedCount);
  }

  [Fact]
  public void Request_PastCapacity_AdmitsTheMeasuredPeak_AtASixtyTwoHostCap()
  {
    // Issue #305's measured peak is 62 concurrent hosts. A run of at least 62 must be ADMITTED
    // — the criterion's own words — so a cap sized from a 32 GB machine admits all of them.
    int cap = HostCapacity.ForMachine(34_101_985_280L);
    HostAdmissionGate gate = new(cap);
    List<Guid> ids = [.. Enumerable.Range(0, 62).Select(_ => Guid.NewGuid())];

    foreach (Guid id in ids)
    {
      Assert.True(gate.Request(id).IsAdmitted, "the measured peak of 62 hosts must be admitted.");
    }

    Assert.Equal(62, gate.Live);
    Assert.Equal(0, gate.ParkedCount);
  }

  [Fact]
  public void Release_HandsTheSlotDirectlyToTheParkHead()
  {
    // The #238 admission rule, applied to hosts: releasing a slot must not let a FRESH start
    // race the queue head, or a parked start starves.
    HostAdmissionGate gate = new(capacity: 1);
    Guid first = Guid.NewGuid();
    Guid parked = Guid.NewGuid();
    _ = gate.Request(first);
    _ = gate.Request(parked);

    HostAdmission? handed = gate.Release();

    Assert.NotNull(handed);
    Assert.Equal(parked, handed.RecordId);
    Assert.True(handed.IsAdmitted);
    Assert.Equal(1, gate.Live); // never overshoots the cap
    Assert.Equal(0, gate.ParkedCount);
  }

  [Fact]
  public void Release_WithNothingParked_DropsTheLiveCount()
  {
    HostAdmissionGate gate = new(capacity: 2);
    _ = gate.Request(Guid.NewGuid());

    Assert.Null(gate.Release());
    Assert.Equal(0, gate.Live);
  }

  [Fact]
  public void Release_NeverDrivesTheLiveCountNegative()
  {
    HostAdmissionGate gate = new(capacity: 2);

    _ = gate.Release();
    _ = gate.Release();

    Assert.Equal(0, gate.Live);
  }

  [Fact]
  public void Withdraw_RemovesAParkedStart_WithoutReleasingASlot()
  {
    // A cancelled parked start never held a slot, so withdrawing it must not advance the queue.
    HostAdmissionGate gate = new(capacity: 1);
    _ = gate.Request(Guid.NewGuid());
    Guid parked = Guid.NewGuid();
    _ = gate.Request(parked);

    Assert.True(gate.Withdraw(parked));
    Assert.Equal(0, gate.ParkedCount);
    Assert.Equal(1, gate.Live);
    Assert.False(gate.Withdraw(parked)); // already gone
  }

  // A zero cap parks every start forever; clamping it silently would hide the caller's bug.
  [Fact]
  public void Constructor_RefusesAZeroCapacity()
      => _ = Assert.Throws<ArgumentOutOfRangeException>(() => new HostAdmissionGate(0));

  [Fact]
  public void Request_IsFirstComeFirstServed()
  {
    HostAdmissionGate gate = new(capacity: 1);
    _ = gate.Request(Guid.NewGuid());
    Guid a = Guid.NewGuid();
    Guid b = Guid.NewGuid();
    _ = gate.Request(a);
    _ = gate.Request(b);

    HostAdmission? first = gate.Release();
    HostAdmission? second = gate.Release();

    Assert.Equal(a, first!.RecordId);
    Assert.Equal(b, second!.RecordId);
  }
}



namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Controller lease arbitration (task 16): first authenticated connection wins,
///     others get controller_busy with details.owner; release frees it; the OwnerLost
///     callback fires exactly when the OWNER leaves, which is the A3 hook the input
///     layer uses to cancel holds and clear synthetic modifiers.</summary>
public class ControllerLeaseTests
{
  [Fact]
  public void FirstAcquire_Wins_SecondAcquire_IsBusyWithOwner()
  {
    ControllerLease lease = new();
    Assert.True(lease.TryAcquire(1).Acquired);
    LeaseAcquireResult second = lease.TryAcquire(2);
    Assert.False(second.Acquired);
    Assert.Equal(1, second.Owner);
  }

  [Fact]
  public void Release_ByOwner_FreesLease_NextAcquireWins()
  {
    ControllerLease lease = new();
    _ = lease.TryAcquire(7);
    Assert.True(lease.Release(7));
    Assert.True(lease.TryAcquire(9).Acquired);
    Assert.Equal(9, lease.Owner);
  }

  [Fact]
  public void Release_ByNonOwner_DoesNotFreeLease()
  {
    ControllerLease lease = new();
    _ = lease.TryAcquire(3);
    Assert.False(lease.Release(4));
    Assert.Equal(3, lease.Owner);
    LeaseAcquireResult busy = lease.TryAcquire(5);
    Assert.False(busy.Acquired);
  }

  [Fact]
  public void OwnerLost_RaisedExactlyWhenOwnerReleases_NotForNonOwner()
  {
    ControllerLease lease = new();
    List<int> lost = [];
    lease.OwnerLost += lost.Add;
    _ = lease.TryAcquire(1);
    _ = lease.Release(2);
    Assert.Empty(lost);
    _ = lease.Release(1);
    _ = lease.TryAcquire(1);
    _ = lease.Release(1);
    Assert.Equal([1, 1], lost);
  }

  [Fact]
  public void OwnerLost_ReachesSubscriber_InputStateDoubleClearsHolds()
  {
    // A3: on owner disconnect the lease signals OnOwnerLost; the input layer
    // (task 17) subscribes to cancel ACTIVE holds and clear synthetic modifiers.
    // The double records the contract the input layer must satisfy.
    ControllerLease lease = new();
    FakeInputState input = new();
    lease.OwnerLost += input.OnOwnerLost;
    _ = lease.TryAcquire(11);
    Assert.True(input.ClearCount == 0, "no callback before the owner is lost");
    _ = lease.Release(11);
    Assert.Equal(1, input.ClearCount);
    Assert.Equal(11, input.LastClearedOwner);
  }

  [Fact]
  public void AcquireResult_FreeLease_OwnerIsNull()
  {
    ControllerLease lease = new();
    LeaseAcquireResult result = lease.TryAcquire(1);
    Assert.True(result.Acquired);
    Assert.Null(result.Owner);
  }

  private sealed class FakeInputState
  {
    public int ClearCount { get; private set; }
    public int LastClearedOwner { get; private set; }
    public void OnOwnerLost(int ownerConnectionId)
    {
      ClearCount++;
      LastClearedOwner = ownerConnectionId;
    }
  }
}

using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The process's own liveness signal, which is what an orphan sweep consults before it
///     decides a persisted Running row has no owner.
///     <para>THE COUNT IS THE POINT. A resume builds a second container for an id the previous
///     container may still be releasing, so a plain set would let the older release erase the
///     newer registration and report a live session dead for the rest of the process's life.
///     </para></summary>
public class LiveRootSessionsTests
{
  private static AgentId Id() => AgentId.NewId();

  [Fact]
  public void RegisteredId_IsLive_UntilTheLeaseIsDisposed()
  {
    LiveRootSessions registry = new();
    AgentId id = Id();

    IDisposable lease = registry.Register(id);
    Assert.Contains(id.Value, registry.Snapshot());

    lease.Dispose();
    Assert.DoesNotContain(id.Value, registry.Snapshot());
  }

  [Fact]
  public void TwoLeasesOnOneId_StayLiveUntilBothAreDisposed()
  {
    LiveRootSessions registry = new();
    AgentId id = Id();

    IDisposable first = registry.Register(id);
    IDisposable second = registry.Register(id);
    first.Dispose();

    // THE RESUME CASE: the older container released, the newer one still holds the session.
    Assert.Contains(id.Value, registry.Snapshot());

    second.Dispose();
    Assert.DoesNotContain(id.Value, registry.Snapshot());
  }

  [Fact]
  public void DisposingOneLeaseTwice_ReleasesOnlyOnce()
  {
    LiveRootSessions registry = new();
    AgentId id = Id();

    IDisposable first = registry.Register(id);
    _ = registry.Register(id);
    first.Dispose();
    first.Dispose();

    // The second hold is untouched by the first lease's second disposal: a container disposed
    // by both the shell and the app-wide shutdown must not release somebody else's hold.
    Assert.Contains(id.Value, registry.Snapshot());
  }

  [Fact]
  public void Snapshot_IsACopy_TheCallerCannotMutate()
  {
    LiveRootSessions registry = new();
    _ = registry.Register(Id());

    IReadOnlyCollection<Guid> snapshot = registry.Snapshot();
    IReadOnlyCollection<Guid> later = registry.Snapshot();

    _ = Assert.Single(snapshot);
    _ = Assert.Single(later);
    Assert.NotSame(snapshot, later);
  }

  [Fact]
  public void Carrier_ReleasesTheHold_WhenTheContainerIsDisposed()
  {
    LiveRootSessions registry = new();
    AgentId id = Id();
    LiveSessionCarrier carrier = new(registry);

    carrier.Fill(id);
    Assert.Contains(id.Value, registry.Snapshot());

    carrier.Dispose();
    Assert.DoesNotContain(id.Value, registry.Snapshot());
  }

  [Fact]
  public void Carrier_WithNoFill_DisposesQuietly()
  {
    LiveRootSessions registry = new();
    LiveSessionCarrier carrier = new(registry);

    // A container torn down before its root id was minted is a session that never became live.
    carrier.Dispose();

    Assert.Empty(registry.Snapshot());
  }
}

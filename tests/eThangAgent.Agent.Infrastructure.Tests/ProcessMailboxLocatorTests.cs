using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>W3.2 locator matrix: a process-wide locator resolves an id to the OWNING
///     container's live mailbox, consulting sources in registration order (first hit
///     wins); unknown ids resolve to none — the honest NotRunning answer for an agent
///     no live container (or only other processes) holds. Each source may name its
///     container's event stream; EventsFor resolves the OWNER's stream so a
///     cross-container delivery can be audited on the target's side (W3.3).</summary>
public class ProcessMailboxLocatorTests
{
  private sealed class FakeMailbox : IAgentMailbox
  {
    public List<PendingMessage> Delivered { get; } = [];

    public Result<bool> Deliver(PendingMessage message)
    {
      Delivered.Add(message);
      return Result.Success(true);
    }

    public IReadOnlyList<PendingMessage> Drain() => Delivered;

    public int UnreadCount => Delivered.Count;
  }

  private sealed class FakeEvents : IAgentEvents
  {
    public IDisposable Subscribe(IAgentEventSubscriber subscriber) => new Lease();

    public void Publish(ChildEvent evt) { }

    private sealed class Lease : IDisposable
    {
      public void Dispose() { }
    }
  }

  [Fact]
  public void TryGet_FirstRegisteringSourceWins()
  {
    ProcessMailboxLocator locator = new();
    FakeMailbox first = new();
    FakeMailbox second = new();
    locator.AddSource(id => id.Value == It ? first : null);
    locator.AddSource(id => id.Value == It ? second : null);

    Assert.Same(first, locator.TryGet(new AgentId(It)));
  }

  private static readonly Guid It = Guid.NewGuid();

  [Fact]
  public void TryGet_UnknownId_ResolvesNone()
  {
    ProcessMailboxLocator locator = new();
    locator.AddSource(_ => null);

    Assert.Null(locator.TryGet(new AgentId(Guid.NewGuid())));
  }

  [Fact]
  public void TryGet_NoSources_ResolvesNone()
  {
    ProcessMailboxLocator locator = new();

    Assert.Null(locator.TryGet(new AgentId(Guid.NewGuid())));
  }

  [Fact]
  public void TryGet_MixedOwnership_FindsTheOwningContainer()
  {
    ProcessMailboxLocator locator = new();
    Guid aChild = Guid.NewGuid();
    Guid bChild = Guid.NewGuid();
    FakeMailbox aMailbox = new();
    FakeMailbox bMailbox = new();
    locator.AddSource(id => id.Value == aChild ? aMailbox : null); // container A's view
    locator.AddSource(id => id.Value == bChild ? bMailbox : null); // container B's view

    Assert.Same(aMailbox, locator.TryGet(new AgentId(aChild)));
    Assert.Same(bMailbox, locator.TryGet(new AgentId(bChild)));
  }

  [Fact]
  public void EventsFor_ReturnsTheOwningContainersStream()
  {
    ProcessMailboxLocator locator = new();
    Guid owned = Guid.NewGuid();
    FakeEvents stream = new();
    locator.AddSource(id => id.Value == owned ? new FakeMailbox() : null, stream);

    Assert.Same(stream, locator.EventsFor(new AgentId(owned)));
    Assert.Null(locator.EventsFor(new AgentId(Guid.NewGuid())));
  }

  [Fact]
  public void EventsFor_SourceWithoutStream_ResolvesNone()
  {
    ProcessMailboxLocator locator = new();
    Guid owned = Guid.NewGuid();
    locator.AddSource(id => id.Value == owned ? new FakeMailbox() : null);

    Assert.Null(locator.EventsFor(new AgentId(owned)));
  }

  [Fact]
  public void AddSource_NullSource_Throws()
  {
    ProcessMailboxLocator locator = new();

    _ = Assert.Throws<ArgumentNullException>(() => locator.AddSource(null!));
  }
}

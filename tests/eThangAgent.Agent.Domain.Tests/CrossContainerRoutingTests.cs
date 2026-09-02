using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>W3.1/W3.4 unit matrix: agent.route consults the cross-container mailbox
///     locator when (and only when) the local runtime fails NotRunning. Without a
///     locator the behavior is byte-for-byte today's; the receipt contract
///     ("delivered to=... link=...") and the error codes (NotRunning, MailboxFull)
///     render unchanged no matter which half delivered. A cross-container delivery
///     publishes MessageDeliveredEvent on the TARGET's stream with direction
///     "cross-container" (3.3's host-side audit trail).</summary>
public class CrossContainerRoutingTests
{
  private static AgentRecord Parent(int depth = 1)
      => AgentRecord.Spawned(AgentId.NewId(), null, depth, "m/sub", "parent", "task", DateTimeOffset.UtcNow);

  private sealed class FakeRuntime : IAgentRuntime
  {
    public HashSet<Guid> Running { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not used")));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
        => Running.Contains(id.Value)
            ? Result.Success(true)
            : Result.Failure<bool>(new DomainError("NotRunning", $"agent '{id}' is not running."));

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  private sealed class FakeQueries(params AgentRecord[] records) : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(records.FirstOrDefault(r => r.Id == id) is { } record
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"agent '{id}' not found.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", "not used")));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<AgentRecord>>(new DomainError("Unused", "not exercised here")));
  }

  private sealed class FakeSpawnCommand : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("Unused", "not exercised here")));
  }

  /// <summary>Locator over one live-mailbox map with a probe counter — the composition
  ///     locator's shape in miniature, instrumented to prove WHEN it is consulted.</summary>
  private sealed class CountingLocator(IReadOnlyDictionary<Guid, IAgentMailbox> mailboxes) : IAgentMailboxLocator
  {
    public int Probes { get; private set; }

    public IAgentMailbox? TryGet(AgentId id)
    {
      Probes++;
      return mailboxes.TryGetValue(id.Value, out IAgentMailbox? mailbox) ? mailbox : null;
    }
  }

  /// <summary>A domain-only event stream capturing published events — the target-stream
  ///     audit probe. Subscribing returns a no-op lease; the provider is the publisher.</summary>
  private sealed class EventCapture : IAgentEvents
  {
    public List<ChildEvent> Events { get; } = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber) => new NoLease();

    public void Publish(ChildEvent evt) => Events.Add(evt);

    private sealed class NoLease : IDisposable
    {
      public void Dispose() { }
    }
  }

  private static (AgentCapabilityProvider Provider, FakeRuntime Runtime, CountingLocator? Locator, AgentLinkRegistry Links) Make(
      AgentRecord parent, IReadOnlyDictionary<Guid, IAgentMailbox>? foreignMailboxes = null,
      IAgentEvents? events = null, bool withLocator = true)
  {
    FakeRuntime runtime = new();
    CountingLocator? locator = withLocator && foreignMailboxes is not null
        ? new CountingLocator(foreignMailboxes)
        : null;
    AgentLinkRegistry links = new();
    AgentCapabilityProvider provider = new(
        new FakeSpawnCommand(), new FakeQueries(parent), () => parent,
        runtime, links, locator: locator,
        eventsFor: events is null ? null : _ => events);
    return (provider, runtime, locator, links);
  }

  /// <summary>Consents a link named after the target (resolution must succeed for the
  ///     delivery-half matrix; the consent gate itself is pinned elsewhere).</summary>
  private static string LinkFor(AgentLinkRegistry links, Guid target)
  {
    string name = "peer" + target.ToString("N")[..8];
    Result<LinkAddress> consented = links.Link(name, "container-b", target.ToString("D"), consented: true);
    Assert.True(consented.IsSuccess);
    return name;
  }

  [Fact]
  public async Task Route_LocalRuntimeHit_NeverConsultsTheLocator()
  {
    (AgentCapabilityProvider provider, FakeRuntime runtime, CountingLocator? locator, AgentLinkRegistry links) =
        Make(Parent(), new Dictionary<Guid, IAgentMailbox>());
    Guid local = Guid.NewGuid();
    _ = runtime.Running.Add(local);
    string name = LinkFor(links, local);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ $"{{\"name\":\"{name}\",\"text\":\"hi\"}}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    // The local runtime delivered: the locator was never probed.
    Assert.Equal(0, locator!.Probes);
  }

  [Fact]
  public async Task Route_LocalMiss_LocatorHit_DeliversIntoTheForeignMailbox()
  {
    Guid foreignChild = Guid.NewGuid();
    BoundedAgentMailbox foreignMailbox = new();
    (AgentCapabilityProvider provider, _, _, AgentLinkRegistry links) = Make(Parent(),
        new Dictionary<Guid, IAgentMailbox> { [foreignChild] = foreignMailbox });
    string name = LinkFor(links, foreignChild);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ $"{{\"name\":\"{name}\",\"text\":\"hello across\",\"urgency\":\"attention\"}}",
        TestContext.Current.CancellationToken);

    // The receipt contract is unchanged no matter which half delivered.
    Assert.False(result.IsError);
    Assert.Equal($"delivered to={foreignChild:D} link={name}", result.Content);
    IReadOnlyList<PendingMessage> drained = foreignMailbox.Drain();
    PendingMessage message = Assert.Single(drained);
    Assert.Equal("hello across", message.Text);
    Assert.Equal(MessageUrgency.Attention, message.Urgency);
    Assert.Equal("parent:parent", message.Sender); // SenderLabel(record) contract, unchanged
  }

  [Fact]
  public async Task Route_LocalMiss_LocatorMiss_FailsNotRunning()
  {
    Guid nobody = Guid.NewGuid();
    (AgentCapabilityProvider provider, _, CountingLocator? locator, AgentLinkRegistry links) = Make(Parent(),
        new Dictionary<Guid, IAgentMailbox>());
    string name = LinkFor(links, nobody);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ $"{{\"name\":\"{name}\",\"text\":\"hello\"}}",
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotRunning]", result.Content, StringComparison.Ordinal);
    // The locator WAS consulted (local missed first) and found nothing.
    Assert.Equal(1, locator!.Probes);
  }

  [Fact]
  public async Task Route_LocatorMailboxFull_FailsMailboxFull()
  {
    Guid foreignChild = Guid.NewGuid();
    BoundedAgentMailbox full = new(capacity: 1);
    _ = full.Deliver(new PendingMessage("occupant", MessageUrgency.Normal, DateTimeOffset.UtcNow, "x"));
    (AgentCapabilityProvider provider, _, _, AgentLinkRegistry links) = Make(Parent(),
        new Dictionary<Guid, IAgentMailbox> { [foreignChild] = full });
    string name = LinkFor(links, foreignChild);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ $"{{\"name\":\"{name}\",\"text\":\"hello\"}}",
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [MailboxFull]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Route_CrossContainerDelivery_PublishesCrossContainerEventOnTheTargetStream()
  {
    Guid foreignChild = Guid.NewGuid();
    BoundedAgentMailbox foreignMailbox = new();
    EventCapture capture = new();
    (AgentCapabilityProvider provider, _, _, AgentLinkRegistry links) = Make(Parent(),
        new Dictionary<Guid, IAgentMailbox> { [foreignChild] = foreignMailbox }, events: capture);
    string name = LinkFor(links, foreignChild);

    _ = await provider.InvokeAsync("route",
        /*lang=json,strict*/ $"{{\"name\":\"{name}\",\"text\":\"audit me\"}}",
        TestContext.Current.CancellationToken);

    MessageDeliveredEvent published = Assert.Single(capture.Events.OfType<MessageDeliveredEvent>());
    Assert.Equal(foreignChild, published.ChildId.Value);
    Assert.Equal("cross-container", published.Direction);
  }

  [Fact]
  public async Task Route_NoLocator_LocalMiss_FailsNotRunning_LegacyShape()
  {
    Guid gone = Guid.NewGuid();
    (AgentCapabilityProvider provider, _, CountingLocator? locator, AgentLinkRegistry links) =
        Make(Parent(), withLocator: false);
    string name = LinkFor(links, gone);

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ $"{{\"name\":\"{name}\",\"text\":\"hello\"}}",
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotRunning]", result.Content, StringComparison.Ordinal);
    Assert.Null(locator);
  }

  [Fact]
  public async Task Route_UnlinkedName_FailsNotLinked_BeforeAnyDelivery()
  {
    (AgentCapabilityProvider provider, _, CountingLocator? locator, _) =
        Make(Parent(), new Dictionary<Guid, IAgentMailbox>());

    CapabilityInvocationResult result = await provider.InvokeAsync("route",
        /*lang=json,strict*/ "{\"name\":\"ghost\",\"text\":\"hello\"}",
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotLinked]", result.Content, StringComparison.Ordinal);
    // Resolution failed: no delivery half was ever consulted.
    Assert.Equal(0, locator!.Probes);
  }
}

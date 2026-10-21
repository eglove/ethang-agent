using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public sealed class ResourceGovernorTests
{
  private const long Physical = 16_000_000_000L; // budget total 8 GB, one conversation

  private sealed class FixedResources(long bytes) : IMachineResources
  {
    public Result<long> TotalPhysicalBytes() => Result.Success(bytes);
  }

  private sealed class FixedObservations(params ResourceObservation[] observations) : IResourceObservationSource
  {
    public List<ResourceObservation> Current { get; } = [.. observations];

    public IReadOnlyList<ResourceObservation> Observe() => Current;
  }

  private sealed class RecordingEnactment : IResourceEnactment
  {
    public List<(string ConversationId, ResourceAction Action)> Calls { get; } = [];

    public Task<Result<string>> EnactAsync(string conversationId, ResourceAction action, string reason, CancellationToken ct = default)
    {
      Calls.Add((conversationId, action));
      return Task.FromResult(Result.Success("enacted"));
    }
  }

  private sealed class RecordingEvents : IWatchdogEventStore
  {
    public List<WatchdogEvent> Events { get; } = [];

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      Events.Add(evt);
      return Task.FromResult(Result.Success("ok"));
    }

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
      => Task.FromResult(Result.Success(0));
  }

  private static ResourceGovernor Build(
      IMachineResources resources, IResourceObservationSource observations,
      RecordingEnactment enactment, RecordingEvents events, int openConversations = 1)
      => new(resources, observations, enactment, events,
          new WatchdogOptions(), TimeProvider.System, () => openConversations);

  [Fact]
  public async Task TickAsync_UnderNotice_EnactsNothing()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    ResourceGovernor governor = Build(new FixedResources(Physical),
        new FixedObservations(new ResourceObservation("c1", 100_000_000L, [])), enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Empty(enactment.Calls);
    Assert.Empty(events.Events);
  }

  [Fact]
  public async Task TickAsync_AtNotice_SteersAndRecords()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    ResourceGovernor governor = Build(new FixedResources(Physical),
        new FixedObservations(new ResourceObservation("c1", 5_000_000_000L, [])), enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal([("c1", ResourceAction.Steer)], enactment.Calls);
    Assert.Contains(events.Events, e => e.Kind == WatchdogEventKind.ResourceNotice);
  }

  [Fact]
  public async Task TickAsync_SecondTickInSameEpisode_DoesNotRepeatSteer()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    ResourceGovernor governor = Build(new FixedResources(Physical),
        new FixedObservations(new ResourceObservation("c1", 5_000_000_000L, [])), enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    _ = Assert.Single(enactment.Calls);
  }

  [Fact]
  public async Task TickAsync_RisingAtEmergency_EscalatesToNextRung()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    FixedObservations observations = new(new ResourceObservation("c1", 5_000_000_000L, []));
    ResourceGovernor governor = Build(new FixedResources(Physical), observations, enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);                       // Notice -> Steer
    observations.Current[0] = new ResourceObservation("c1", 7_000_000_000L, []);
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);                       // Act -> KillProgram
    observations.Current[0] = new ResourceObservation("c1", 7_800_000_000L, []);
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);                       // Emergency, rising -> ReclaimMemory

    Assert.Equal(
        [ResourceAction.Steer, ResourceAction.KillProgram, ResourceAction.ReclaimMemory],
        enactment.Calls.Select(c => c.Action));
  }

  [Fact]
  public async Task TickAsync_BackUnderNotice_ResetsEpisode()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    FixedObservations observations = new(new ResourceObservation("c1", 5_000_000_000L, []));
    ResourceGovernor governor = Build(new FixedResources(Physical), observations, enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    observations.Current[0] = new ResourceObservation("c1", 10_000_000L, []);
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    observations.Current[0] = new ResourceObservation("c1", 5_000_000_000L, []);
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(2, enactment.Calls.Count);
    Assert.All(enactment.Calls, c => Assert.Equal(ResourceAction.Steer, c.Action));
  }

  [Fact]
  public async Task TickAsync_TwoConversations_ActsOnTheLargestFirst()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    ResourceGovernor governor = Build(new FixedResources(Physical),
        new FixedObservations(
            new ResourceObservation("small", 100_000_000L, []),
            new ResourceObservation("huge", 5_000_000_000L, [])), enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal("huge", Assert.Single(enactment.Calls).ConversationId);
  }

  [Fact]
  public async Task TickAsync_MachineMemoryUnavailable_EnactsNothingAndRecordsNothing()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    IMachineResources failing = new FailingResources();
    ResourceGovernor governor = Build(failing,
        new FixedObservations(new ResourceObservation("c1", 5_000_000_000L, [])), enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Empty(enactment.Calls);
  }

  [Fact]
  public async Task TickAsync_RecoveringFromPressure_RecordsTheEpisodeEnd()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    FixedObservations observations = new(new ResourceObservation("c1", 5_000_000_000L, []));
    ResourceGovernor governor = Build(new FixedResources(Physical), observations, enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    observations.Current[0] = new ResourceObservation("c1", 10_000_000L, []);
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The audit trail shows the episode's END, not only its escalation.
    Assert.Contains(events.Events, e => e.Kind == WatchdogEventKind.ResourceRecovered);
  }

  [Fact]
  public async Task TickAsync_ConversationClosedAndReopened_StartsAFreshEpisode()
  {
    RecordingEnactment enactment = new();
    RecordingEvents events = new();
    FixedObservations observations = new(new ResourceObservation("c1", 5_000_000_000L, []));
    ResourceGovernor governor = Build(new FixedResources(Physical), observations, enactment, events);

    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true); // c1: Notice -> Steer
    observations.Current.Clear();
    observations.Current.Add(new ResourceObservation("c2", 100_000_000L, []));
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true); // c1 closed: its episode is forgotten
    observations.Current.Clear();
    observations.Current.Add(new ResourceObservation("c1", 5_000_000_000L, []));
    _ = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true); // c1 reopened: a NEW episode

    Assert.Equal(2, enactment.Calls.Count);
    Assert.All(enactment.Calls, call => Assert.Equal(ResourceAction.Steer, call.Action));
  }

  private sealed class FailingResources : IMachineResources
  {
    public Result<long> TotalPhysicalBytes()
      => Result.Failure<long>(new DomainError("MachineMemoryUnavailable", "probe failed"));
  }
}

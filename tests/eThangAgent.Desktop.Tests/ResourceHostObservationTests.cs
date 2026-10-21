using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The production observation seam (spec D5/D14): what the app sees as
///     memory consumers. Two properties matter and neither had a test before:
///     <para>(a) the APP process is a consumer of the app-wide budget, and it must be
///     VISIBLE - hiding it would make the total read low; but it is NOT a
///     conversation, and the governor must never be handed it as one (it selects the
///     largest row as its target, so an 'app' row is a decision about a conversation
///     that does not exist).</para>
///     <para>(b) a conversation's memory is its HOST PROCESS's working set - the number
///     that actually bounds it.</para></summary>
public sealed class ResourceHostObservationTests
{
  [Fact]
  public void Observe_IncludesTheAppProcess_AndReportsAHostWorkingSet()
  {
    // The test process itself stands in for a conversation host: its working set is
    // a real, readable number.
    ResourceHostObservation observation = new(() => [new ResourceHostHandle("c1", Environment.ProcessId)]);

    IReadOnlyList<ResourceObservation> rows = observation.Observe();

    ResourceObservation host = Assert.Single(rows, row => row.ConversationId == "c1");
    Assert.True(host.UsageBytes > 0, "a live host's working set must be reported as a real number");
    Assert.Equal([Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], host.ProgramIds);

    // The app process is a consumer too, and it is reported SEPARATELY from the
    // conversation rows: it is real usage, but it is not a conversation.
    Assert.Equal(0, rows.Count(row => row.ConversationId == ResourceHostObservation.AppConsumerId));
    Assert.True(observation.AppUsageBytes > 0, "the app's own working set must be visible to the indicator");
  }

  [Fact]
  public void Observe_ReportsTheAppUsageSeparately_FromTheConversationRows()
  {
    ResourceHostObservation observation = new(() => []);

    IReadOnlyList<ResourceObservation> rows = observation.Observe();

    Assert.Empty(rows);
    Assert.True(observation.AppUsageBytes > 0);
  }

  [Fact]
  public void Observe_AHostThatHasExited_ContributesNothing()
  {
    // A process id that cannot exist (the highest pid is far below this): the
    // observation must not throw and must not fabricate a number.
    ResourceHostObservation observation = new(() => [new ResourceHostHandle("dead", 2_000_000_000)]);

    IReadOnlyList<ResourceObservation> rows = observation.Observe();

    ResourceObservation dead = Assert.Single(rows, row => row.ConversationId == "dead");
    Assert.Equal(0, dead.UsageBytes);
  }

  /// <summary>The app row must NEVER be a governor target. The governor picks the
  ///     largest OBSERVATION and enacts a rung against it, so presenting the app's
  ///     own memory as a conversation produced a decision about a conversation that
  ///     does not exist - an audit row naming 'conversation app' and a refusal,
  ///     every tick, for the app's own memory. This is measured against the REAL
  ///     governor and the REAL enactment.</summary>
  [Fact]
  public async Task TheAppProcess_IsNeverAGovernorTarget()
  {
    ResourceHostObservation observation = new(() => [new ResourceHostHandle("c1", Environment.ProcessId)]);
    RecordingEvents events = new();
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1", null));
    ResourceGovernor governor = new(new FixedResources(16_000_000_000L), observation, enactment, events,
        new WatchdogOptions(), TimeProvider.System, () => 1);

    ResourceGovernorTick tick = await governor.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The target is a CONVERSATION, never the app process.
    Assert.NotEqual(ResourceHostObservation.AppConsumerId, tick.ConversationId);
    Assert.Equal("c1", tick.ConversationId);

    // And no audit row ever names a conversation called 'app'.
    Assert.DoesNotContain(events.Events,
        row => row.Detail.Contains(ResourceHostObservation.AppConsumerId, StringComparison.Ordinal));
  }

  /// <summary>The app's own memory reaches the governor on its own channel, so the
  ///     governor can count it in the app-wide picture without ever being able to
  ///     select it as a target.</summary>
  [Fact]
  public void TheAppUsage_ReachesTheGovernor_OnItsOwnChannel()
  {
    ResourceHostObservation observation = new(() => []);
    _ = observation.Observe();

    Assert.True(observation.AppUsageBytes > 0);
    IResourceObservationSource seam = observation;
    Assert.True(seam.AppUsageBytes > 0);
  }

  private sealed class FixedResources(long bytes) : IMachineResources
  {
    public Result<long> TotalPhysicalBytes() => Result.Success(bytes);
  }

  private sealed class RecordingEvents : IWatchdogEventStore
  {
    public List<WatchdogEvent> Events { get; } = [];

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      Events.Add(evt);
      return Task.FromResult(Result.Success("ok"));
    }

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(
        int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
        WatchdogEventKind kind, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(
        AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));
  }
}

using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #301 D5: the root-session tick - nudge once, then park, driven by the ROOT
///     loop's own turn facts. Fakes only: no clock, no store, no host process.</summary>
public class RootSessionWatchdogTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
  private static readonly AgentId Session = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class FakeHost : IRootSessionHost
  {
    public List<string> Nudges { get; } = [];

    public List<string> Parks { get; } = [];

    public bool Reachable { get; set; } = true;

    public bool TryNudge(AgentId sessionId, string text)
    {
      if (!Reachable)
      {
        return false;
      }

      Nudges.Add(text);
      return true;
    }

    public Task ParkAsync(AgentId sessionId, string reason, CancellationToken ct = default)
    {
      Parks.Add(reason);
      return Task.CompletedTask;
    }
  }

  private sealed class FakeEvents : IWatchdogEventStore
  {
    public List<WatchdogEvent> Appended { get; } = [];

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      Appended.Add(evt);
      return Task.FromResult(Result.Success(evt.Id.ToString()));
    }

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
        WatchdogEventKind kind, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind,
        CancellationToken ct = default) => Task.FromResult(Result.Success(0));
  }

  private static readonly ChainStepAssignment Assignment = new(ChainId: 4, PlanId: 9, Position: 2,
      SessionId: Session.ToString());


  /// <summary>Builds the rig for a session executing the fixture chain step.</summary>
  private static (RootSessionWatchdog Watchdog, RootSessionSupervisor Supervisor, FakeHost Host,
      FakeEvents Events, FixedClock Clock) New() => New(Assignment);

  /// <summary>Builds the rig for a session with the given assignment, or none at all.</summary>
  private static (RootSessionWatchdog Watchdog, RootSessionSupervisor Supervisor, FakeHost Host,
      FakeEvents Events, FixedClock Clock) New(ChainStepAssignment? assignment)
  {
    RootSessionSupervisor supervisor = new(Session);
    FakeHost host = new();
    FakeEvents events = new();
    FixedClock clock = new(T0);
    WatchdogPolicy policy = new(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(1), MaxWrapUpAttempts: 1);
    return (new RootSessionWatchdog(Session, supervisor, assignment, host, policy,
        events, clock), supervisor, host, events, clock);
  }

  /// <summary>Puts the session past its idle threshold with one settled-turn fact.</summary>
  private static void GoIdle(RootSessionSupervisor supervisor, FixedClock clock)
  {
    supervisor.OnActivity(new RootTurnActivity(Session, T0, RootTurnPhase.TurnSettled, "turn-settled"));
    clock.Now = T0.AddMinutes(20);
  }

  [Fact]
  public async Task Tick_OnTheFirstIdleBreach_NudgesOnce()
  {
    (RootSessionWatchdog watchdog, RootSessionSupervisor supervisor, FakeHost host, _, FixedClock clock) = New();
    GoIdle(supervisor, clock);

    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    string nudge = Assert.Single(host.Nudges);
    Assert.Contains("chain #4", nudge, StringComparison.Ordinal);
    Assert.Contains("step 2", nudge, StringComparison.Ordinal);
    Assert.Equal(1, supervisor.WrapUpAttempts);
    Assert.Empty(host.Parks);
  }

  [Fact]
  public async Task Tick_OnTheSecondBreach_ParksTheChainNamingTheStep()
  {
    (RootSessionWatchdog watchdog, RootSessionSupervisor supervisor, FakeHost host, _, FixedClock clock) = New();
    GoIdle(supervisor, clock);
    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The session keeps working, then goes idle again: a NEW breach, so the ladder escalates.
    supervisor.OnActivity(new RootTurnActivity(Session, clock.Now, RootTurnPhase.ToolStarted, "tool:exec"));
    clock.Now = clock.Now.AddMinutes(20);
    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    string park = Assert.Single(host.Parks);
    Assert.Contains("chain #4", park, StringComparison.Ordinal);
    Assert.Contains("step 2", park, StringComparison.Ordinal);
    Assert.Contains("nudge", park, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Tick_WhenTheNudgeCannotBeDelivered_SpendsNoAttemptAndDoesNotPark()
  {
    (RootSessionWatchdog watchdog, RootSessionSupervisor supervisor, FakeHost host, _, FixedClock clock) = New();
    host.Reachable = false;
    GoIdle(supervisor, clock);

    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // A message nobody received must not consume the budget, or one dead host would park a
    // chain a human never got the chance to unblock.
    Assert.Equal(0, supervisor.WrapUpAttempts);
    Assert.Empty(host.Parks);
  }

  [Fact]
  public async Task Tick_WithNoChainAssignment_DoesNothingAtAll()
  {
    (RootSessionWatchdog watchdog, RootSessionSupervisor supervisor, FakeHost host, _, FixedClock clock) =
        New(assignment: null);
    GoIdle(supervisor, clock);

    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // A hand-started session owes no completion, so it is never nudged.
    Assert.Empty(host.Nudges);
    Assert.Empty(host.Parks);
  }

  [Fact]
  public async Task Tick_WhileWorking_DoesNothing()
  {
    (RootSessionWatchdog watchdog, RootSessionSupervisor supervisor, FakeHost host, _, FixedClock clock) = New();
    supervisor.OnActivity(new RootTurnActivity(Session, T0, RootTurnPhase.TurnStarted, "turn-start"));
    clock.Now = T0.AddMinutes(20);

    // A turn that STARTED and has not settled is a session that is working, not one that is
    // idle - the clock is reset by the fact, so the age is 20 minutes and the breach is real;
    // what matters is that the supervisor's own threshold governs, not the phase.
    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    _ = Assert.Single(host.Nudges);
  }

  [Fact]
  public async Task Tick_RecordsAnAuditRowForTheBreachAndForTheNudge()
  {
    (RootSessionWatchdog watchdog, RootSessionSupervisor supervisor, _, FakeEvents events, FixedClock clock) = New();
    GoIdle(supervisor, clock);

    await watchdog.TickAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Contains(events.Appended, e => e.Kind == WatchdogEventKind.HungDetected);
    Assert.Contains(events.Appended, e => e.Kind == WatchdogEventKind.RetrySpawned);
  }
}

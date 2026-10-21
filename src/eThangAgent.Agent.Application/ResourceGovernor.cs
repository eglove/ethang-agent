using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>One tick's outcome, for tests and for the indicator panel.
///     <see cref="Failure"/> is null when nothing was enacted or the enactment succeeded;
///     a non-null value names the enactment failure the governor recorded.</summary>
public sealed record ResourceGovernorTick(
    ResourceLevel Level,
    string? ConversationId,
    ResourceAction Action,
    long UsageBytes,
    long BudgetBytes,
    DomainError? Failure = null);

/// <summary>The resource governor (spec D3/D4/D5): observes every open conversation,
///     derives the machine budget, decides a level and the next ladder rung for the
///     largest offender, enacts it through the owner, and appends the decision to the
///     audit trail. It is deliberately NOT the agent watchdog: that one asks "is a
///     sub-agent stuck?" on a one-minute clock, this one asks "is the machine filling
///     up?" every couple of seconds.
///     <para>Escalation is TREND-driven: a rung is enacted when the level is above Normal
///     AND usage grew since the previous sample of the same episode. The episode's
///     attempted-rung set alone prevents re-enacting a rung, so constant usage enacts one
///     rung and then stops, while usage that keeps growing walks the ladder one rung per
///     rising sample - all five rungs included, however long the episode runs. Usage
///     falling back under the notice level ends the episode, so a recovered conversation
///     can escalate again later.</para></summary>
public sealed class ResourceGovernor(
    IMachineResources machine,
    IResourceObservationSource observations,
    IResourceEnactment enactment,
    IWatchdogEventStore events,
    WatchdogOptions options,
    TimeProvider clock,
    Func<int> openConversations)
{
  private readonly Dictionary<string, Episode> _episodes = new(StringComparer.Ordinal);

  /// <summary>The interval this governor asks its host to tick at. The single source of
  ///     truth is <see cref="WatchdogOptions.ResourceTickInterval"/>; this only surfaces it
  ///     to whoever wires the governor and its loop together.</summary>
  public TimeSpan RecommendedInterval => options.ResourceTickInterval;

  public async Task<ResourceGovernorTick> TickAsync(CancellationToken ct = default)
  {
    Result<long> physical = machine.TotalPhysicalBytes();
    if (!physical.IsSuccess)
    {
      // A failed probe is a named infrastructure condition, never a fabricated budget.
      return new ResourceGovernorTick(ResourceLevel.Normal, null, ResourceAction.None, 0, 0);
    }

    // Named decision: a host that reports zero open conversations still gets one share,
    // because ResourceBudget refuses a non-positive count and a governor that threw on a
    // transient zero would stop watching exactly when the machine is under pressure.
    ResourceBudget budget = new(physical.Value, Math.Max(1, openConversations()));
    IReadOnlyList<ResourceObservation> all = observations.Observe();
    if (all.Count == 0)
    {
      _episodes.Clear();
      return new ResourceGovernorTick(ResourceLevel.Normal, null, ResourceAction.None, 0, budget.TotalBytes);
    }

    PruneClosedEpisodes(all);
    ResourceObservation worst = all.MaxBy(observation => observation.UsageBytes)!;
    if (!_episodes.TryGetValue(worst.ConversationId, out Episode? episode))
    {
      episode = new Episode();
      _episodes[worst.ConversationId] = episode;
    }

    bool rising = worst.UsageBytes > episode.PreviousUsage;
    episode.PreviousUsage = worst.UsageBytes;

    ResourceLevel level = ResourcePolicy.LevelFor(worst.UsageBytes, budget.PerConversationBytes, rising);
    if (level == ResourceLevel.Normal)
    {
      // Recovery resets the episode: the ladder starts from the bottom next time. A
      // conversation that WAS under pressure leaves one closing row behind, so the
      // audit trail shows the episode's end and not only its escalation.
      bool wasUnderPressure = episode.Attempted.Count > 0;
      episode.Attempted.Clear();
      if (wasUnderPressure)
      {
        await AppendAsync(ResourceLevel.Normal, worst, ResourceAction.None, budget, ct).ConfigureAwait(false);
      }

      return new ResourceGovernorTick(level, worst.ConversationId, ResourceAction.None,
          worst.UsageBytes, budget.PerConversationBytes);
    }

    if (!rising)
    {
      // The trend gate: pressure that is not growing has already been answered by this
      // episode's rungs, and pressure that IS growing earns the next one. No cooldown and
      // no level comparison - the attempted set is the only thing that stops a repeat.
      return new ResourceGovernorTick(level, worst.ConversationId, ResourceAction.None,
          worst.UsageBytes, budget.PerConversationBytes);
    }

    ResourceAction action = ResourcePolicy.NextAction(level, episode.Attempted);
    if (!episode.Attempted.Add(action))
    {
      // The ladder is exhausted: NextAction answers a fully-attempted episode with its last
      // rung, and re-enacting that rung on every rising sample would restart the app in a
      // loop. Each rung is enacted at most once per episode.
      return new ResourceGovernorTick(level, worst.ConversationId, ResourceAction.None,
          worst.UsageBytes, budget.PerConversationBytes);
    }

    await AppendAsync(level, worst, action, budget, ct).ConfigureAwait(false);

    // The decision row is already appended above, so the audit trail survives whatever the
    // enactment does. A failure is recorded as its own row and reported on the tick - never
    // swallowed, and never a reason to stop the next tick.
    Result<string> enacted = await enactment
        .EnactAsync(worst.ConversationId, action, Describe(level, worst, budget), ct)
        .ConfigureAwait(false);
    if (!enacted.IsSuccess)
    {
      await AppendEnactmentFailureAsync(worst, action, enacted.Error, ct).ConfigureAwait(false);
      return new ResourceGovernorTick(level, worst.ConversationId, action,
          worst.UsageBytes, budget.PerConversationBytes, enacted.Error);
    }

    return new ResourceGovernorTick(level, worst.ConversationId, action,
        worst.UsageBytes, budget.PerConversationBytes);
  }

  /// <summary>Drops episode state for conversations that are no longer open - the
  ///     governor is long-lived, so it must not accumulate a record per conversation the
  ///     host has ever closed (a memory governor that leaks memory is a bad joke).</summary>
  private void PruneClosedEpisodes(IReadOnlyList<ResourceObservation> open)
  {
    HashSet<string> live = new(open.Select(observation => observation.ConversationId), StringComparer.Ordinal);
    string[] closed = [.. _episodes.Keys.Where(id => !live.Contains(id))];
    foreach (string id in closed)
    {
      _ = _episodes.Remove(id);
    }
  }

  private async Task AppendAsync(ResourceLevel level, ResourceObservation observation,
      ResourceAction action, ResourceBudget budget, CancellationToken ct)
  {
    string detail = Describe(level, observation, budget) + " -> " + action;
    WatchdogEvent row = new(Guid.NewGuid(), null, KindFor(level), detail, 0,
        Math.Round(observation.UsageBytes / (1024.0 * 1024.0), 1), clock.GetUtcNow());
    // Best-effort audit, like every other watchdog writer: a store fault must not turn a
    // memory decision into a crashed tick.
    Result<string> appended = await events.AppendAsync(row, ct).ConfigureAwait(false);
    _ = appended;
  }

  /// <summary>The readable half of a failed enactment: one WatchdogErrored row naming the
  ///     rung and the owner's error, so the audit trail never claims an action that did not
  ///     happen.</summary>
  private async Task AppendEnactmentFailureAsync(ResourceObservation observation,
      ResourceAction action, DomainError error, CancellationToken ct)
  {
    string detail = "conversation " + observation.ConversationId + " -> " + action
        + " failed: " + error.Code + ": " + error.Message;
    WatchdogEvent row = new(Guid.NewGuid(), null, WatchdogEventKind.WatchdogErrored, detail, 0,
        Math.Round(observation.UsageBytes / (1024.0 * 1024.0), 1), clock.GetUtcNow());
    Result<string> appended = await events.AppendAsync(row, ct).ConfigureAwait(false);
    _ = appended;
  }

  private static WatchdogEventKind KindFor(ResourceLevel level) => level switch
  {
    ResourceLevel.Notice => WatchdogEventKind.ResourceNotice,
    ResourceLevel.Act => WatchdogEventKind.ResourceActed,
    ResourceLevel.Emergency => WatchdogEventKind.ResourceEmergency,
    ResourceLevel.Normal => WatchdogEventKind.ResourceRecovered,
    _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown resource level."),
  };

  private static string Describe(ResourceLevel level, ResourceObservation observation, ResourceBudget budget)
  {
    string used = Mb(observation.UsageBytes);
    string share = Mb(budget.PerConversationBytes);
    string total = Mb(budget.TotalBytes);
    return "conversation " + observation.ConversationId + " at " + used + " MB of its "
        + share + " MB share (" + level + "; harness budget " + total + " MB)";
  }

  private static string Mb(long bytes) =>
      (bytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture);

  /// <summary>One conversation's pressure episode: the previous sample (the trend gate's
  ///     input) and the rungs already enacted, so the ladder never repeats a rung inside
  ///     one episode.</summary>
  private sealed class Episode
  {
    public long PreviousUsage { get; set; }

    public HashSet<ResourceAction> Attempted { get; } = [];
  }
}

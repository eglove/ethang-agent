using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>One open conversation's host: the conversation id the governor names, and
///     the supervisor that owns that conversation's host process.</summary>
internal sealed record ResourceHostRegistration(string ConversationId, RemoteHostSupervisor Supervisor);

/// <summary>The indicator's feed: it turns one governor tick into the numbers and
///     words the window chrome shows (spec D14).
///     <para>The FEED is the only thing that reads the audit trail for the indicator:
///     the governor appends its decisions, and this reads them back by kind - the
///     table finally has a reader, which is the whole point of the indicator.</para>
///     <para>READ-ONLY: the feed reports; it never asks the governor to act, and it
///     exposes no action of its own (the user declined a manual reclaim).</para></summary>
internal sealed class ResourceIndicatorFeed(
    ResourceIndicatorViewModel indicator,
    ResourceHostObservation observation,
    IWatchdogEventStore events)
{
  /// <summary>How many audit rows of each kind the indicator shows. The four resource
  ///     kinds are the governor's own decisions; the error kind is a refused or failed
  ///     enactment, which the user must see too - a silent refusal is a lie by
  ///     omission.</summary>
  private const int DecisionLimit = 8;

  private static readonly WatchdogEventKind[] DecisionKinds =
  [
    WatchdogEventKind.ResourceNotice,
    WatchdogEventKind.ResourceActed,
    WatchdogEventKind.ResourceEmergency,
    WatchdogEventKind.ResourceRecovered,
    WatchdogEventKind.WatchdogErrored,
  ];

  /// <summary>Publishes one tick into the indicator. Called on the governor's clock,
  ///     which is a background thread - the indicator is bound to the chrome, so the
  ///     update is marshalled onto the UI thread by the caller.</summary>
  public async Task PublishAsync(ResourceGovernorTick tick, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(tick);

    // A tick that could not derive a budget (a failed machine probe) observed
    // nothing this round: showing the PREVIOUS sample's consumers beside it would
    // describe a sample that is not the one the tick describes.
    IReadOnlyList<ResourceObservation> observations = tick.TotalBytes is null && tick.BudgetBytes == 0
        ? []
        : observation.LastObserved;
    IReadOnlyList<WatchdogEvent> decisions = await ReadDecisionsAsync(ct).ConfigureAwait(false);
    indicator.Update(tick, observations, decisions);
  }

  /// <summary>Reads the governor's recent decisions, newest first, across the kinds
  ///     that carry them. A store fault yields an EMPTY list, never a stale one: the
  ///     indicator must never show decisions that have since been superseded.</summary>
  private async Task<IReadOnlyList<WatchdogEvent>> ReadDecisionsAsync(CancellationToken ct)
  {
    List<WatchdogEvent> decisions = [];
    foreach (WatchdogEventKind kind in DecisionKinds)
    {
      Result<IReadOnlyList<WatchdogEvent>> rows =
          await events.ListRecentByKindAsync(kind, DecisionLimit, ct).ConfigureAwait(false);
      if (rows.IsSuccess)
      {
        decisions.AddRange(rows.Value);
      }
    }

    return [.. decisions.OrderByDescending(row => row.CreatedAt).Take(DecisionLimit)];
  }
}

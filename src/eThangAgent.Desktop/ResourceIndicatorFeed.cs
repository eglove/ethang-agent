using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>One open conversation's host: the conversation id the governor names, and
///     the supervisor that owns that conversation's host process. The supervisor is
///     null when the conversation has no host process yet (its host was never
///     launched, or it died) - a rung that acts on a process then refuses with a
///     named error instead of dereferencing nothing.</summary>
internal sealed record ResourceHostRegistration(string ConversationId, RemoteHostSupervisor? Supervisor)
{
  /// <summary>The conversation's host process id, when one is running. The reclaim rung
  ///     acts on THIS process: a rung keyed by a conversation that trimmed the APP's
  ///     working set had a target and an effect that disagreed.</summary>
  public int? HostProcessId { get; init; }
}

/// <summary>The indicator's feed: it turns one governor tick into the numbers and
///     words the window chrome shows (spec D14).
///     <para>The FEED is the only thing that reads the audit trail for the indicator:
///     the governor appends its decisions, and this reads them back by kind - the
///     table finally has a reader, which is the whole point of the indicator.</para>
///     <para>READ-ONLY: the feed reports; it never asks the governor to act, and it
///     exposes no action of its own (the user declined a manual reclaim).</para>
///     <para>THREAD AFFINITY: the indicator is bound to the chrome, so the update
///     runs on the UI thread. The read of the audit table does NOT - it is a
///     suspending store call - so the read runs wherever it likes and the UPDATE
///     is marshalled back onto the UI thread explicitly. A continuation that
///     resumes on a thread-pool thread would raise PropertyChanged off the UI
///     thread and fault the binding the moment the store's read suspends.</para></summary>
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
  ///     which is a background thread - the update is marshalled onto the UI thread
  ///     here, because the indicator is bound to the chrome.</summary>
  public async Task PublishAsync(ResourceGovernorTick tick, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(tick);

    // A tick that could not derive a budget (a failed machine probe) observed
    // nothing this round: showing the PREVIOUS sample's consumers beside it would
    // describe a sample that is not the one the tick describes.
    IReadOnlyList<ResourceObservation> conversations = tick.TotalBytes is null && tick.BudgetBytes == 0
        ? []
        : observation.LastObserved;

    // The app process is real app-wide usage, so the bar must count it - but it is
    // NOT a conversation: the governor never saw it, and it is shown on its own line.
    List<ResourceObservation> observations = [.. conversations];
    if (tick.TotalBytes is not null || tick.BudgetBytes != 0)
    {
      observations.Add(new ResourceObservation(ResourceHostObservation.AppConsumerId,
          observation.AppUsageBytes, []));
    }
    IReadOnlyList<WatchdogEvent> decisions = await ReadDecisionsAsync(ct).ConfigureAwait(false);

    // The update is UI-AFFINE: the indicator raises PropertyChanged, and the chrome
    // binds to it. The read above may have resumed on any thread, so the update is
    // POSTED to the UI thread - a non-async dispatcher body, so the update itself
    // never leaves the UI thread, and the post never blocks the governor's clock on
    // a dispatcher that might be shutting down.
    Avalonia.Threading.Dispatcher.UIThread.Post(
        () => indicator.Update(tick, observations, decisions));
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

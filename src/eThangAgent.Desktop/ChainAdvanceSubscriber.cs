using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>Turns a COMPLETION FACT into an advance (issue #301 D1).
///     <para>THE TRIGGER IS THE FACT, NOT A POLL: <see cref="PlanChangedEvent"/> already
///     reaches the app carrying the authoring agent id (the Composition's
///     <c>PlanChangePublisher</c> raises it, and the host forwards it over the wire), so a
///     poller would re-derive what is already published and race the write.</para>
///     <para>ONLY A STEP CHANGE IS A COMPLETION. The plan's events are created / steps /
///     status; 'created' and 'status' say nothing about a step, and treating them as
///     completions would advance a chain on a plan rename.</para>
///     <para>THE POSITION IS READ FROM THE RECORD, not from the payload: the event carries
///     the plan id and the author, and the chain store already knows which step THAT author
///     holds. Reading it from the payload would trust a value the event does not carry.</para>
///     <para>FIRE AND FORGET, CONTAINED: the fact arrives on an event-pump thread where an
///     escaping exception has no handler, and the advance's own outcome is a value the chain
///     record already reflects.</para></summary>
internal sealed class ChainAdvanceSubscriber(
    Func<string, ChainAdvanceService?> resolve,
    Action<string>? report = null)
{
  /// <summary>Handles one child event. Everything that is not a step change is ignored.</summary>
  /// <param name="evt">The event.</param>
  /// <param name="workspaceId">The workspace the event belongs to.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task OnEventAsync(ChildEvent evt, string workspaceId, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(evt);
    if (evt is not PlanChangedEvent { Change: "steps" } plan)
    {
      return;
    }

    if (resolve(workspaceId) is not { } advance)
    {
      return;
    }

    // Named decision (CA1031): the advance runs on an event-pump thread; a fault here must
    // never reach the publisher, and the chain record is the durable answer either way.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<ChainAdvance> result = await advance
          .OnCompletionAsync(plan.ChildId.ToString(), plan.PlanId, DateTimeOffset.UtcNow, ct)
          .ConfigureAwait(false);
      if (!result.IsSuccess)
      {
        report?.Invoke($"[chain] advance failed: {result.Error.Code}: {result.Error.Message}");
      }
      else if (result.Value.Code is { } code)
      {
        report?.Invoke($"[chain] advance refused [{code}]: {result.Value.Reason}");
      }
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      report?.Invoke("[chain] advance faulted: " + ex.Message);
    }
  }
}

using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>Turns a TERMINAL SETTLE into a lifecycle decision (issue #303, wired by #323): a
///     chain step's session that settled failed is retried once per the attempt budget, then
///     parks the chain.
///     <para>WHY THE SETTLE FACT AND NOT A POLL: it is the same stream the advance already reads,
///     published by the runtime the moment a run ends. #323's finding was that the whole
///     lifecycle had no caller - the decision rule, the service, and the port all existed and
///     nothing invoked them - so the trigger had to be a fact something already produces.</para>
///     <para>ONLY A FAILED TERMINAL SETTLE COUNTS. A completed run is the ADVANCE's fact, and
///     treating it here would settle the same step twice; a reasonless terminal state (cancelled
///     by the user) is not a failure of the work.</para>
///     <para>FIRE AND FORGET, CONTAINED: the fact arrives on the wire pump, where an escaping
///     exception has no handler, and the chain record is the durable answer either way.</para></summary>
/// <param name="lifecycle">The session's lifecycle service.</param>
/// <param name="report">Surfaces the outcome as a transcript notice.</param>
internal sealed class ChainFailureSubscriber(ChainLifecycleService lifecycle, Action<string>? report = null)
{
  /// <summary>Handles one child event. Everything that is not a failed terminal settle is
  ///     ignored.</summary>
  /// <param name="evt">The event.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task OnEventAsync(ChildEvent evt, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(evt);
    if (evt is not ChildSettledEvent { TerminalStatus: AgentStatus.Failed } settled)
    {
      return;
    }

#pragma warning disable CA1031 // Named decision: the settle arrives on the wire pump.
    try
    {
      Result<ChainFailureOutcome> outcome = await lifecycle
          .OnStepFailedAsync(settled.ChildId.ToString(), DescribeReason(settled),
              settled.At, ct)
          .ConfigureAwait(false);
      if (!outcome.IsSuccess)
      {
        report?.Invoke($"[chain] failure handling failed: {outcome.Error.Code}: {outcome.Error.Message}");
        return;
      }

      // AN ORDINARY SESSION REPORTS NOTHING. The lifecycle answers 'not assigned to any chain
      // step' for every hand-started session, and a notice on each of those would train the user
      // to ignore the one that matters.
      if (outcome.Value.Position is not { } position)
      {
        return;
      }

      report?.Invoke(outcome.Value.Parked
          ? $"[chain] parked at step {position}: {outcome.Value.Reason}"
          : $"[chain] step {position} failed and was resumed for another attempt");
    }
    catch (Exception ex)
    {
      report?.Invoke("[chain] failure handling faulted: " + ex.Message);
    }
#pragma warning restore CA1031
  }

  /// <summary>What the settle recorded about why the run failed, or null when it recorded
  ///     nothing. The lifecycle stores this as the step's outcome, so a human reading the
  ///     parked chain learns the actual reason rather than only that something went wrong.</summary>
  private static string? DescribeReason(ChildSettledEvent settled) =>
      settled.Reason is { } reason ? reason.ToString() : null;
}

/// <summary>Adapts the session's event stream to <see cref="ChainFailureSubscriber"/>: the
///     stream is synchronous and the handling is async, so each fact is dispatched and its task
///     observed. The same shape as <c>ChainAdvanceEventSink</c>, for the same reason.</summary>
internal sealed class ChainFailureEventSink(ChainFailureSubscriber subscriber, AgentId sessionId)
    : IAgentEventSubscriber
{
  public void OnEvent(ChildEvent evt)
  {
    // ONLY THIS SESSION'S OWN SETTLE. The stream carries every child's facts, and a failed
    // SUBAGENT must not be mistaken for a failed chain step.
    if (evt.ChildId != sessionId)
    {
      return;
    }

    _ = Task.Run(() => subscriber.OnEventAsync(evt, CancellationToken.None));
  }
}

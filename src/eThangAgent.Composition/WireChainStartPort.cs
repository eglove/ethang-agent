using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>The composition adapter for <c>IChainStartPort</c> (issue #313): emits a
///     session-request carrying a chain step assignment through the live turn's channel and
///     awaits the app's answer.
///     <para>WHY THE SAME CHANNEL AS <c>new_session</c>: the act is identical — start a
///     session in this workspace and answer with its id — and the app is the only place a
///     session can be started, because a session is a tab cell, a container, and a host
///     process. A second envelope kind would be a second codec and a second handler for one
///     behaviour.</para>
///     <para>THE CREATING SESSION IS NOT CONTINUED (D5). The request names no predecessor:
///     the app starts step 1 in the workspace the plan belongs to, and the session that
///     created the chain is free to close the moment this returns.</para>
///     <para>A REFUSAL IS THE APP'S, VERBATIM. This port translates nothing: the app's typed
///     code (TabClosed, SessionLaunchFailed, BriefUnsupported, ...) reaches the caller
///     unchanged, so the model reads the real cause.</para></summary>
public sealed class WireChainStartPort(NewSessionRequestChannel channel) : IChainStartPort
{
  private readonly NewSessionRequestChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));

  /// <inheritdoc />
  public async Task<Result<string>> StartFirstStepAsync(ChainStepAssignment assignment,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(assignment);
    if (!assignment.IsExecutable)
    {
      return Result.Failure<string>(new DomainError("InvalidAssignment",
          "a chain launch needs a chain id, a plan id, and a step position; " + assignment + " names none."));
    }

    // EMPTY PROMPT: a chain step starts SEED-ONLY. The brief IS what it works from, and
    // firing a turn with text this harness invents would be a handoff written by a model,
    // which D5 forbids.
    // THE ROLE TRAVELS when the caller read it from the record; an assignment that did not
    // is an implementer step, which is the same default the record itself applies.
    ChainStepRole role = assignment.Role ?? ChainStepRole.Implement;
    SessionRequestPayload payload = new(Guid.NewGuid().ToString("N"), Prompt: "", HandoffSummary: null)
    {
      ChainStep = new SessionRequestChainStep(assignment.ChainId, assignment.PlanId,
          assignment.Position, role.Encoded),
    };
    return await _channel.RequestAsync(payload, ct).ConfigureAwait(false);
  }
}

using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>The composition adapter for <c>IChainResumeLaunchPort</c>: emits a session-request
///     carrying a chain step assignment through the live turn's channel and awaits the app's
///     answer.
///     <para>WHY THE SAME CHANNEL AS A CHAIN CREATION: the act is identical - start a session in
///     this workspace and answer with its id - and the app is the only place a session can be
///     started. A second envelope kind would be a second codec and a second handler for one
///     behaviour.</para>
///     <para>THE <c>Resumed</c> FLAG IS THE WHOLE DIFFERENCE (issue #303's exit). A creation
///     and an advance name a predecessor to continue; a resume has none, and the flag is what
///     tells the app not to look for one. Everything else - the brief, the kickoff line, the
///     role - travels exactly as a creation's does.</para>
///     <para>A REFUSAL IS THE APP'S, VERBATIM: the typed code reaches the caller unchanged, so
///     the model reads the real cause.</para></summary>
public sealed class WireChainResumeLaunch(SessionRequestChannel channel) : IChainResumeLaunchPort
{
  private readonly SessionRequestChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));

  /// <inheritdoc />
  public async Task<Result<string>> StartResumedSessionAsync(ChainStepAssignment assignment,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(assignment);
    if (!assignment.IsExecutable)
    {
      return Result.Failure<string>(new DomainError("InvalidAssignment",
          "a resumed chain launch needs a chain id, a plan id, and a step position; " + assignment + " names none."));
    }

    ChainStepRole role = assignment.Role ?? ChainStepRole.Implement;
    SessionRequestPayload payload = new(Guid.NewGuid().ToString("N"),
        Prompt: ChainKickoff.Line, HandoffSummary: null)
    {
      ChainStep = new SessionRequestChainStep(assignment.ChainId, assignment.PlanId,
          assignment.Position, role.Encoded),
      Resumed = true,
    };
    return await _channel.RequestAsync(payload, ct).ConfigureAwait(false);
  }
}

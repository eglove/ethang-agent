using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>The composition adapter for <c>IAgentLaunchCommand</c> (issue #338): emits a
///     session-request through the live turn's channel and answers with the new session's
///     id, exactly as a chain launch and a compaction handoff do.
///     <para>THE THIRD CALLER OF ONE SEAM. The act is the same one every time - start a
///     session in this workspace and answer with its id - and only the shell can perform
///     it, because a session is a tab cell, a container, and a host process. A second
///     envelope kind for 'a model wants an agent' would be a second codec, a second host
///     emitter, and a second app handler for one behaviour.</para>
///     <para>WHAT THIS ADAPTER DOES NOT DO: it names no predecessor, stamps no parent id,
///     and records no depth. The payload it builds carries the caller's WORKSPACE (a
///     placement fact) and that is the whole of the relationship. The session that results
///     is a sibling, and nothing downstream can read it as a subordinate.</para>
///     <para>A REFUSAL IS THE APP'S, VERBATIM. This port translates nothing, so the app's
///     typed code (TabClosed, SessionLaunchFailed, SeedUnsupported, ...) reaches the caller
///     unchanged. An unreachable launch fails at once with the channel's own
///     <c>SessionRequestUnreachable</c> rather than waiting out a budget for an answer that
///     is not coming.</para></summary>
public sealed class WireAgentLaunchPort(SessionRequestChannel channel, string workspaceId)
    : IAgentLaunchCommand
{
  private readonly SessionRequestChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));

  /// <summary>The workspace the sibling lands in: the caller's own. Empty is a wiring
  ///     fault rather than a silent fallback, because a launch into an unnamed workspace
  ///     could only be placed by guessing.</summary>
  private readonly string _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
      ? throw new ArgumentException("workspaceId must be non-empty.", nameof(workspaceId))
      : workspaceId;

  /// <inheritdoc />
  public async Task<Result<AgentId>> StartAsync(AgentLaunchRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (!request.IsLaunchable)
    {
      return Result.Failure<AgentId>(new DomainError("InvalidActionInput",
          "a launch needs a non-empty prompt: an agent started with nothing to do would park "
          + "until a human typed into it, which is not a launch."));
    }

    // THE CALLER'S TURN CARRIES THE REQUEST. The channel is connected for the lifetime of the
    // live root turn, so a launch issued from inside an exec script rides the turn that is
    // already running - no second connection and no second budget.
    SessionRequestPayload payload = new(Guid.NewGuid().ToString("N"),
        Prompt: request.Prompt, HandoffSummary: null)
    {
      SiblingOfWorkspaceId = _workspaceId,
      Label = request.Label,
      Model = request.Model,
    };

    Result<string> answered = await _channel.RequestAsync(payload, ct).ConfigureAwait(false);
    if (!answered.IsSuccess)
    {
      return Result.Failure<AgentId>(answered.Error);
    }

    // THE ANSWER IS AN AGENT ID, STRICTLY PARSED. A session id that will not parse means the
    // app answered with something this build cannot address, which the caller must learn now
    // rather than by following a malformed id into agent.result.
    return Guid.TryParseExact(answered.Value, "D", out Guid parsed)
        ? Result.Success(new AgentId(parsed))
        : Result.Failure<AgentId>(new DomainError("SessionResponseMalformed",
            "the app answered a launch with a session id that is not a GUID: '" + answered.Value + "'."));
  }
}

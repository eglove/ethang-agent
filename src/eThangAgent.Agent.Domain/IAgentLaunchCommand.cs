namespace eThangAgent.AgentDomain;

/// <summary>What a model asks for when it starts an agent (issue #338): the prompt the new
///     agent begins from, and the two display facts a cell can state about it.
///     <para>IT NAMES NO PREDECESSOR, AND THERE IS NOWHERE TO PUT ONE. Starting an agent is
///     not delegation: the agent this creates is the caller's SIBLING, placed in the same
///     workspace and shown beside it. The type therefore carries the work and nothing that
///     could be read back as a hierarchy, and it holds no reference to the agent it starts,
///     because the answer is a value the caller receives rather than a child it keeps.</para>
///     <para>THE WORKSPACE IS NOT A MEMBER EITHER. The caller's own workspace scopes the
///     launch - the same rule the cell subscription uses to place a cell - so a sibling
///     cannot be started into a workspace the caller is not working in.</para></summary>
/// <param name="Prompt">The new agent's first turn. Non-empty: an agent started with
///     nothing to do would park until a human typed into it, which is not a launch.</param>
/// <param name="Label">The caption its cell shows, or null for the workspace name the
///     cell falls back to. This is the same label a record persisted by any other route
///     carries.</param>
/// <param name="Model">The model to serve the new agent, or null to inherit the
///     workspace's own choice - the ordinary launch.</param>
public sealed record AgentLaunchRequest(string Prompt, string? Label = null, string? Model = null)
{
  /// <summary>True when this request names the work an agent starts from. Only a request
  ///     with something to do is launchable; everything else is an argument error the
  ///     caller reports rather than a launch that quietly does nothing.</summary>
  public bool IsLaunchable => !string.IsNullOrWhiteSpace(Prompt);
}

/// <summary>Where a model's request to start an agent is carried (issue #338).
///     <para>WHY A PORT: the Agent Domain decides that a session should exist and what its
///     first turn says; only the shell can create one, because a session is a tab cell, a
///     container, and a host process. The capability asks through this seam, so the
///     decision stays testable with a fake and the shell never re-derives the rule.
///     </para>
///     <para>IT RETURNS, IT DOES NOT JOIN. The result is the new agent's id and nothing
///     else: reading the outcome is agent.status or agent.result, which are the caller's
///     own later acts. A launch that awaited its agent would be supervision by another
///     name, which is exactly what a sibling is not.</para>
///     <para>A REFUSAL IS A VALUE, never an exception: every failure is a typed
///     <see cref="SharedKernel.DomainError"/> the caller renders as a tool result. The
///     channel's own codes travel verbatim - <c>SessionRequestUnreachable</c> when no live
///     turn can carry the request, so an unreachable launch fails immediately rather than
///     hanging.</para></summary>
public interface IAgentLaunchCommand
{
  /// <summary>Starts one agent from <paramref name="request"/> and returns its id, or a
  ///     typed failure.</summary>
  /// <param name="request">The work the new agent starts from.</param>
  /// <param name="ct">Cancellation.</param>
  Task<SharedKernel.Result<AgentId>> StartAsync(AgentLaunchRequest request,
      CancellationToken ct = default);
}

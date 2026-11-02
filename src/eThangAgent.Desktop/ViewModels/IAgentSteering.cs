using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What a host-agent cell's chat input can do to its agent (issue #330): steer the run
///     that is live, or resume the one that has settled.
///     <para>WHY A PORT RATHER THAN THE CHANNEL DIRECTLY: the cell renders a surface and the
///     channel is a container-side wire seam; keeping them apart is what lets a test drive the
///     cell's whole submit path - acceptance, refusal, resume - with no host, no pipe, and no
///     process. It is also where the two shapes of one act meet: steering a live run and
///     resuming a settled one are the same human gesture, so they belong to one seam rather
///     than to two call sites that could drift.</para>
///     <para>NULL IS AN ANSWER: a cell built without a seam (a headless fixture, or a host
///     container that wired no wire channel) says so in a notice instead of accepting input
///     that would go nowhere.</para></summary>
internal interface IAgentSteering
{
  /// <summary>Delivers <paramref name="text"/> into the agent's own mailbox in its own host.
  ///     Fails with the host's typed reason when the agent has no live run there, its mailbox
  ///     refused, or the host did not answer - never silently.</summary>
  /// <param name="agentId">The agent to steer.</param>
  /// <param name="text">The message text.</param>
  /// <param name="messageId">The identity the cell pinned its echo under.</param>
  Task<Result<bool>> SteerAsync(AgentId agentId, string text, Guid messageId);

  /// <summary>Resumes a settled agent: relaunches a host for its record and starts a turn with
  ///     <paramref name="text"/> as the continuation prompt. This is the act
  ///     <c>agent.resume</c> already performs, reached from the cell's input.</summary>
  /// <param name="agentId">The agent to resume.</param>
  /// <param name="text">The continuation prompt.</param>
  Task<Result<AgentId>> ResumeAsync(AgentId agentId, string text);
}

/// <summary>The production steering for one cell (issue #330), built from the session container
///     that launched the agent.
///     <para>STEERING RIDES THE CONTAINER'S OWN WIRE CHANNEL: that channel already addresses the
///     host process where the agent runs, so no second connection is opened. RESUME RIDES THE
///     CONTAINER'S OWN RUNTIME: under the chain model a settled agent's host is gone, so resume
///     means relaunching one for the record - the exact act <c>agent.resume</c> performs, and
///     the one a parked chain step needs. Two paths, one meaning: the cell's input.</para>
///     <para>BOTH SEAMS ARE RESOLVED FROM THE CONTAINER, never from the cell: the cell owns no
///     provider, and a cell that captured one would keep a disposed host alive.</para></summary>
internal sealed class HostAgentSteering(IRootTurnChannel channel, IAgentRuntime runtime) : IAgentSteering
{
  private readonly IRootTurnChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

  /// <summary>Delivers <paramref name="text"/> to the agent, asking the runtime that owns its
  ///     host FIRST and falling back to the container's own channel (issue #330 review F3).
  ///     <para>WHY THE RUNTIME COMES FIRST: an agent started by a chain step is launched into a
  ///     host of its own, and the runtime that launched it is the only object holding a
  ///     connection to that host. The container's channel speaks to the CONTAINER's host, which
  ///     runs the conversation and not the agent - so a steer sent there is answered NotRunning
  ///     by a process that was never going to serve it.</para>
  ///     <para>THE FALLBACK IS NARROW ON PURPOSE: only
  ///     <see cref="RuntimeErrors.SteeringUnavailable"/> means 'this runtime has no route at
  ///     all'. A host's own refusal (a full box, a settled run) is a real answer about the
  ///     agent, and retrying it on another connection would deliver a message the first host
  ///     already reported it could not take.</para></summary>
  /// <param name="agentId">The agent to steer.</param>
  /// <param name="text">The message text.</param>
  /// <param name="messageId">The identity the cell pinned its echo under.</param>
  public async Task<Result<bool>> SteerAsync(AgentId agentId, string text, Guid messageId)
  {
    Result<bool> routed = await _runtime.SteerAgentAsync(agentId, text, messageId).ConfigureAwait(false);
    return routed.IsSuccess || routed.Error.Code != RuntimeErrors.SteeringUnavailable
        ? routed
        : await _channel.SteerAgentAsync(agentId, text, messageId).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<Result<AgentId>> ResumeAsync(AgentId agentId, string text)
      => _runtime.Resume(agentId, text, CancellationToken.None);
}

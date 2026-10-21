using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>The app-facing port for one conversation's root turn (spec D6). The app runs
///     NO agent work: a turn travels to this conversation's host process and its events
///     stream back, so the rendered surface is unchanged while the agent itself lives
///     elsewhere.
/// <para>WHY A PORT AND NOT THE RUNTIME ITSELF: the view-model is a UI concern and the wire
///     runtime is a transport concern. A session container with no wire runtime (a headless
///     or test host that deliberately opted into the in-process runtime) has no channel at
///     all, which is exactly how the app decides that a turn must go out of process — the
///     presence of the channel IS the decision, never a flag that can drift from it.</para>
/// <para>CONTRACT: the implementation mints a fresh generation per turn and delivers an
///     event only when both the turn id and the generation match the live turn, so a stale
///     settle from a timed-out predecessor can never complete the current turn. Every
///     failure is a typed <see cref="Result{T}"/> value — a host that never answers fails
///     <c>TurnUnaddressable</c> on the idle bound, so a turn can never hang.</para></summary>
public interface IRootTurnChannel
{
  /// <summary>Sends one root turn and routes its events, in arrival order, to
  ///     <paramref name="onEvent"/>. The returned value is the turn's settled payload: the
  ///     answer, or the host's <c>Error [&lt;code&gt;]: &lt;message&gt;</c> line for a turn the
  ///     host itself refused or faulted. A transport-level failure is a failed Result whose
  ///     error code names the fault.</summary>
  Task<Result<string>> SendRootTurnAsync(RootTurnRequest request, Action<RootTurnEvent> onEvent,
      CancellationToken ct = default);
}

/// <summary>The production adapter: forwards to the conversation's wire runtime
///     (<see cref="RemoteAgentRuntime.SendRootTurnAsync"/>), which owns the turn identity,
///     the settle matching, and the idle/ceiling bounds.</summary>
public sealed class RemoteRootTurnChannel(RemoteAgentRuntime runtime) : IRootTurnChannel
{
  private readonly RemoteAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

  /// <inheritdoc />
  public Task<Result<string>> SendRootTurnAsync(RootTurnRequest request, Action<RootTurnEvent> onEvent,
      CancellationToken ct = default)
      => _runtime.SendRootTurnAsync(request, onEvent, ct);
}

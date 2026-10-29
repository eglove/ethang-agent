using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>What the harness needs from the shell to enact an advance (issue #301 D2). The
///     application layer decides; the shell owns the tab, the container, and the host process,
///     so it supplies the two acts the decision needs and nothing else.
///     <para>WHY A PORT AND NOT A DIRECT CALL: the advance runs in the app (that is where the
///     completion fact lands and where sessions are started), while the decision belongs to
///     the Agent Domain. This interface is the seam between them, so the advance can be tested
///     with fakes and the shell never has to re-derive the rule.</para></summary>
public interface IChainAdvanceHost
{
  /// <summary>Closes the session that completed its step (D4). Best effort by contract: the
  ///     transcript is already persisted, so a teardown fault must not stop the chain.</summary>
  /// <param name="sessionId">The session to close.</param>
  /// <param name="ct">Cancellation.</param>
  Task CloseSessionAsync(string sessionId, CancellationToken ct = default);

  /// <summary>Starts a session for <paramref name="assignment"/> and returns its id. A failed
  ///     start is a typed error, never an exception.
  ///     <para><paramref name="predecessorSessionId"/> names the session that completed the
  ///     previous step. The successor continues in THAT session's tab, which is what puts the
  ///     advance in the right workspace: a chain belongs to one workspace, and the session that
  ///     completed its step was running in it.</para></summary>
  /// <param name="assignment">The chain step the new session is started for.</param>
  /// <param name="predecessorSessionId">The session whose completion triggered the advance.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment, string predecessorSessionId,
      CancellationToken ct = default);
}

/// <summary>Enacts the mechanical advance (issue #301): a completion fact in, the next
///     session started out. No model decides whether to continue.
///     <para>THE RULE IS <see cref="ChainAdvancePolicy"/>; this class only performs what the
///     rule decided, so there is exactly one place the rule lives and exactly one place the
///     side effects live.</para>
///     <para>ORDER IS LOAD-BEARING. The completed step is settled and the next step CLAIMED
///     before the old session is closed and the new one started: the claim is what makes the
///     new session's ownership check pass, and closing first would leave a window where the
///     chain has no owner for the step it is about to run.</para>
///     <para>A refusal is returned as a <see cref="ChainAdvance"/> value, never thrown: the
///     model receives it as a tool result and can correct itself.</para></summary>
public sealed class ChainAdvanceService(ChainService chains, IChainAdvanceHost host)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainAdvanceHost _host = host ?? throw new ArgumentNullException(nameof(host));

  /// <summary>Handles one completion fact: settle the step the session was assigned, then
  ///     advance the cursor.
  ///     <para>THE POSITION COMES FROM THE RECORD, never from the fact. A plan-change event
  ///     carries the plan id and the authoring agent; it does not carry a chain position. The
  ///     chain already knows which step that author holds, so reading it here is the only
  ///     answer that cannot disagree with the assignment.</para>
  ///     <para>WHICH SESSION MAY COMPLETE WHICH STEP IS ENFORCED AT THE WRITE: the plan
  ///     provider's ownership gate refuses a Done on a step the session does not hold, so a
  ///     fact that reaches this method is one the gate already allowed. This method therefore
  ///     advances; it does not re-litigate.</para></summary>
  /// <param name="sessionId">The session that reported the completion (the fact's author).</param>
  /// <param name="planId">The plan the fact names, so a chain on a DIFFERENT plan is not advanced.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainAdvance>> OnCompletionAsync(string sessionId, int planId,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    Result<Chain?> found = await _chains.FindBySessionAsync(sessionId, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      return Result.Failure<ChainAdvance>(found.Error);
    }

    // The session is not executing a chain step at all: an ordinary hand-started session, or
    // one whose step was already settled. Not an error - there is simply nothing to advance.
    if (found.Value is not { } chain)
    {
      return Result.Success(ChainAdvance.Refused(ChainAdvanceCode.ForeignStep,
          $"session {sessionId} is not assigned to any chain step, so there is nothing to advance."));
    }

    // A chain executes ONE plan. A plan write on any other plan is somebody else's work.
    if (chain.PlanId != planId)
    {
      return Result.Success(ChainAdvance.Refused(ChainAdvanceCode.ForeignStep,
          $"chain #{chain.Id} executes plan #{chain.PlanId}, not plan #{planId}; that write does not advance this chain."));
    }

    ChainStep? assigned = chain.Steps.FirstOrDefault(step =>
        string.Equals(step.SessionId, sessionId, StringComparison.Ordinal));
    return assigned is null
      ? Result.Success(ChainAdvance.Refused(ChainAdvanceCode.ForeignStep,
          $"chain #{chain.Id} holds no step for session {sessionId}; there is nothing to advance."))
      : await AdvanceAsync(chain, assigned.Position, sessionId, now, ct).ConfigureAwait(false);
  }

  /// <summary>Settles the step, then advances. Extracted so a caller that already holds the
  ///     chain (the ownership check's own tests) drives the same path.</summary>
  /// <param name="chain">The chain the session is assigned to.</param>
  /// <param name="position">The step position the completion named.</param>
  /// <param name="sessionId">The session that reported the completion.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainAdvance>> AdvanceAsync(Chain chain, int position, string sessionId,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(chain);
    Result<Chain> settled = await _chains.SettleStepAsync(chain.Id, position, ChainStepStatus.Done,
        ChainActor.Session(sessionId), outcome: null, now, ct).ConfigureAwait(false);
    if (!settled.IsSuccess)
    {
      return Result.Failure<ChainAdvance>(settled.Error);
    }

    ChainAdvance decision = ChainAdvancePolicy.Decide(settled.Value, position, sessionId, now);
    if (!decision.IsActionable)
    {
      // A refusal leaves the session OPEN on purpose: the model must be able to read why its
      // completion was rejected and correct itself. Closing it here would answer a question
      // nobody could then ask.
      return Result.Success(decision);
    }

    // D4: the session that completed its step is closed - ONCE. A sequential advance closes
    // it by REPLACING it (the successor takes its cell, and the replacement retires the
    // predecessor), so closing here as well would tear the same session down twice. A chain
    // that completes or blocks has no successor, so that is where the close is explicit.
    return decision.Kind == ChainAdvanceKind.CompleteChain
        ? Result.Success(await CompleteAsync(settled.Value, sessionId, now, ct).ConfigureAwait(false))
        : Result.Success(await StartStepAsync(settled.Value, decision.Position!.Value, sessionId, now, ct)
            .ConfigureAwait(false));
  }

  /// <summary>Claims the next step for a NEW session and starts it. The claim happens
  ///     BEFORE the start so the step is unavailable to any other claimant during the launch
  ///     window; the claim is then REASSIGNED to the real id, which spends no extra attempt.
  ///     <para>WHY A PLACEHOLDER AT ALL: the chain record requires a session id to claim, and
  ///     the id does not exist until the session does.</para></summary>
  private async Task<ChainAdvance> StartStepAsync(Chain chain, int position, string predecessorSessionId,
      DateTimeOffset now, CancellationToken ct)
  {
    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains.ClaimStepAsync(chain.Id, position, provisional, now, ct)
        .ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return ChainAdvance.Refused(claimed.Error.Code, claimed.Error.Message);
    }

    Result<string> started = await _host
        .StartSessionAsync(new ChainStepAssignment(chain.Id, chain.PlanId, position, null),
            predecessorSessionId, ct)
        .ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      // The launch failed: RELEASE the claim so the step is available again rather than
      // parked behind a session that never existed. The attempt count is kept - a failed
      // launch is still an attempt the bound must see.
      _ = await _chains.ReleaseStepAsync(chain.Id, position, ChainActor.Harness, now, ct)
          .ConfigureAwait(false);
      return ChainAdvance.Refused(started.Error.Code, started.Error.Message);
    }

    // REASSIGN to the real id: the placeholder existed only to hold the step during launch,
    // and reassigning must not spend a second attempt.
    _ = await _chains.ReassignStepAsync(chain.Id, position, started.Value, now, ct)
        .ConfigureAwait(false);
    return new ChainAdvance(ChainAdvanceKind.StartStep, position, null, started.Value);
  }

  /// <summary>Moves a fully settled chain to Completed, with the harness as the actor (D6).
  ///     A refusal here is the chain's own consistency rule (#300's completion invariant),
  ///     reported rather than forced.</summary>
  private async Task<ChainAdvance> CompleteAsync(Chain chain, string sessionId, DateTimeOffset now,
      CancellationToken ct)
  {
    await _host.CloseSessionAsync(sessionId, ct).ConfigureAwait(false);
    Result<Chain> completed = await _chains
        .SetStatusAsync(chain.Id, ChainStatus.Completed, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return completed.IsSuccess
        ? new ChainAdvance(ChainAdvanceKind.CompleteChain, null, null, null)
        : ChainAdvance.Refused(completed.Error.Code, completed.Error.Message);
  }
}

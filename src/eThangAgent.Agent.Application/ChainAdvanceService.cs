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
public sealed class ChainAdvanceService(ChainService chains, IChainAdvanceHost host,
    ChainReviewerGate? reviewers = null, IAgentStore? agents = null,
    IChainReviewSource? reviews = null, IChainCompletionGate? completionGate = null)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainAdvanceHost _host = host ?? throw new ArgumentNullException(nameof(host));
  private readonly ChainReviewerGate? _reviewers = reviewers;
  private readonly IAgentStore? _agents = agents;
  private readonly IChainReviewSource? _reviews = reviews;

  // The completion gate (issue #304 D1). THE SAME GATE the Plan Domain asks, one level up: a
  // chain claiming Completed is a completion claim, so it needs a gate that passed since the
  // newest workspace change. Optional, so a container with no gate machinery behaves exactly
  // as it did before.
  private readonly IChainCompletionGate? _completionGate = completionGate;

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
    // THE REVIEWER'S VERDICT IS READ BEFORE THE STEP SETTLES (issue #320). The reviewer was
    // always told to record one; the advance used to write outcome: null and discard it, so
    // the cursor had nothing to read and a denial could not exist.
    Result<ChainReviewRecord?> review = await ReadReviewAsync(chain, position, sessionId, ct)
        .ConfigureAwait(false);
    if (!review.IsSuccess)
    {
      return Result.Failure<ChainAdvance>(review.Error);
    }

    // THE OUTCOME CARRIES THE VERDICT for a review step and stays NULL for an implementer
    // step, which is byte-identical to the pre-#320 behaviour (#306 pins that text).
    string? outcome = review.Value is { } recorded ? ChainReviewNotes.Render(recorded.Findings) : null;
    Result<Chain> settled = await _chains.SettleStepAsync(chain.Id, position, ChainStepStatus.Done,
        ChainActor.Session(sessionId), outcome, now, ct).ConfigureAwait(false);
    if (!settled.IsSuccess)
    {
      return Result.Failure<ChainAdvance>(settled.Error);
    }

    // A REVIEW STEP'S CURSOR IS THE VERDICT (issue #320), not the plain 'next available step'
    // rule: a denial moves the cursor BACK, and the round-trip cap parks rather than looping.
    if (IsReviewStep(settled.Value, position))
    {
      return await DecideOnReviewAsync(settled.Value, position, sessionId, review.Value, now, ct)
          .ConfigureAwait(false);
    }

    ChainAdvance decision = ChainAdvancePolicy.Decide(settled.Value, position, sessionId, now);
    if (decision.Kind == ChainAdvanceKind.Blocked)
    {
      // A BLOCKED CHAIN IS PARKED, not left Running (issue #303 D3/D9). Nothing is available to
      // start and nothing can complete, so the work is waiting for a human - and a chain that
      // stayed Running here would sit forever while every surface reported it as in progress.
      // 'Waiting for a human' and 'broken' must not look alike, which is what Parked is for.
      return Result.Success(await ParkBlockedAsync(settled.Value, decision, now, ct)
          .ConfigureAwait(false));
    }

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

  /// <summary>Reads the review a session recorded, when the step it holds is a review step.
  ///     <para>AN IMPLEMENT STEP READS NOTHING. Asking the review store for a step that judges
  ///     nothing would spend a query on every ordinary completion and let a stray review row
  ///     from some other session change what an implementer's completion does.</para>
  ///     <para>A STORE FAULT IS REPORTED, never treated as 'no review': a cursor that read an
  ///     unreadable verdict as an absent one would refuse the advance with the wrong reason and
  ///     leave the reviewer's work unexplained.</para></summary>
  /// <param name="chain">The chain the step belongs to.</param>
  /// <param name="position">The 1-based step position being completed.</param>
  /// <param name="sessionId">The session that reported the completion.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainReviewRecord?>> ReadReviewAsync(Chain chain, int position,
      string sessionId, CancellationToken ct)
    => _reviews is null || !IsReviewStep(chain, position)
        ? Result.Success<ChainReviewRecord?>(null)
        : await _reviews.FindForSessionAsync(chain.PlanId, sessionId, ct).ConfigureAwait(false);

  /// <summary>True when the step at <paramref name="position"/> judges work rather than doing
  ///     it. A position the chain does not hold is NOT a review step: the aggregate refuses it
  ///     by name, and guessing here would report a role for a step that does not exist.
  ///     </summary>
  /// <param name="chain">The chain to read the role from.</param>
  /// <param name="position">The 1-based step position.</param>
  private static bool IsReviewStep(Chain chain, int position)
    => chain.StepAt(position)?.Role.IsReview == true;

  /// <summary>Enacts <see cref="ChainReviewPolicy"/> for a settled review step (issue #320):
  ///     advance, send the step back for a fix, or park at the round-trip cap.
  ///     <para>THE DECISION IS PURE AND THIS IS NOT: the rule lives in the domain so it is
  ///     unit-testable, and the side effects live here so there is exactly one place they
  ///     happen - the <see cref="ChainAdvancePolicy"/> split, applied to the review path.</para>
  ///     <para>A REFUSAL LEAVES THE SESSION OPEN, exactly as the advance's refusals do: the
  ///     reviewer must be able to read why its completion was rejected and correct itself, and
  ///     the step is already Done so nothing is half-written.</para></summary>
  /// <param name="chain">The chain as it stands after the step settled.</param>
  /// <param name="position">The review step's 1-based position.</param>
  /// <param name="sessionId">The reviewing session.</param>
  /// <param name="review">The verdict it recorded, or null when it recorded none.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainAdvance>> DecideOnReviewAsync(Chain chain, int position,
      string sessionId, ChainReviewRecord? review, DateTimeOffset now, CancellationToken ct)
  {
    ChainStep? target = chain.FixTargetFor(position);
    ChainReviewDecision decision = ChainReviewPolicy.Decide(review, target?.Position,
        target?.ReviewAttempts ?? 0);
    return decision.Kind switch
    {
      ChainReviewDecisionKind.SendBack =>
          Result.Success(await SendBackAsync(chain, decision, sessionId, now, ct)
              .ConfigureAwait(false)),
      ChainReviewDecisionKind.ParkAtCap =>
          Result.Success(await ParkAtCapAsync(chain, decision, now, ct).ConfigureAwait(false)),
      ChainReviewDecisionKind.Advance => Result.Success(await AdvancePastReviewAsync(
          chain, position, sessionId, now, ct).ConfigureAwait(false)),
      ChainReviewDecisionKind.Refused => Result.Success(ReviewRefusal(decision)),
      _ => Result.Failure<ChainAdvance>(UnknownDecision(decision)),
    };
  }

  /// <summary>A refused review reported as a refusal the model can act on, never thrown:
  ///     the reviewer reads it and corrects itself (the <see cref="ChainAdvance"/> contract).
  ///     </summary>
  /// <param name="decision">The refusal decision.</param>
  private static ChainAdvance ReviewRefusal(ChainReviewDecision decision) =>
      ChainAdvance.Refused(decision.Code ?? ChainReviewCode.NoVerdict,
          decision.Reason ?? "the review could not be acted on.");

  /// <summary>A decision kind outside the closed set is a defect in the policy, reported as a
  ///     named error rather than an escaping exception on an event-pump thread.</summary>
  /// <param name="decision">The unknown decision.</param>
  private static DomainError UnknownDecision(ChainReviewDecision decision) =>
      new("UnknownReviewDecision",
          $"{decision.Kind} is not a chain review decision kind; the set is closed.");

  /// <summary>The reviewer approved: advance the chain exactly as an implementer's completion
  ///     would, and carry any conditions into the next session's brief.
  ///     <para>THE APPROVAL PATH IS THE ORDINARY ADVANCE, deliberately. An approve is not a
  ///     different kind of progress, so it must not acquire its own rules; only the denial has a
  ///     back-edge. Conditions ride the notes the settled step's outcome carries, which the next
  ///     brief renders in its predecessors section (issue #219's surviving rule: conditions
  ///     travel the same path a denial's findings do).</para></summary>
  /// <param name="chain">The chain as it stands after the review settled.</param>
  /// <param name="position">The review step's 1-based position.</param>
  /// <param name="sessionId">The reviewing session.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<ChainAdvance> AdvancePastReviewAsync(Chain chain, int position,
      string sessionId, DateTimeOffset now, CancellationToken ct)
  {
    ChainAdvance next = ChainAdvancePolicy.Decide(chain, position, sessionId, now);
    return next.Kind switch
    {
      ChainAdvanceKind.Blocked => await ParkBlockedAsync(chain, next, now, ct).ConfigureAwait(false),
      ChainAdvanceKind.CompleteChain => await CompleteAsync(chain, sessionId, now, ct)
          .ConfigureAwait(false),
      ChainAdvanceKind.StartStep => await StartStepAsync(chain, next.Position!.Value, sessionId,
          now, ct).ConfigureAwait(false),
      ChainAdvanceKind.Refused => ChainAdvance.Refused(next.Code ?? ChainAdvanceCode.StepNotDone,
          next.Reason ?? "the chain cannot advance past this review."),
      _ => ChainAdvance.Refused("UnknownAdvanceKind",
          $"{next.Kind} is not a chain advance kind; the set is closed."),
    };
  }

  /// <summary>The reviewer denied: reopen the step it judged and start a fix session seeded
  ///     with the cited findings (issue #320).
  ///     <para>ORDER IS LOAD-BEARING, the <see cref="StartStepAsync"/> rule applied to a
  ///     back-edge: the target is REOPENED and then CLAIMED before the session starts, so the
  ///     fix session's ownership check passes and the chain never has a step with no owner. The
  ///     claim is then reassigned to the real id, which spends no second attempt.</para>
  ///     <para>THE REVIEWER'S SESSION IS REPLACED, not closed separately: the fix session is
  ///     started with the reviewer as its predecessor, and a sequential launch replaces that
  ///     session's cell. Closing it here as well would tear one session down twice - the
  ///     <see cref="ChainAdvanceService"/> rule for every sequential move.</para></summary>
  /// <param name="chain">The chain the review settled on.</param>
  /// <param name="decision">The send-back decision, which names the target and the notes.</param>
  /// <param name="reviewerSessionId">The session that reviewed.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<ChainAdvance> SendBackAsync(Chain chain, ChainReviewDecision decision,
      string reviewerSessionId, DateTimeOffset now, CancellationToken ct)
  {
    int target = decision.Target!.Value;
    Result<Chain> reopened = await _chains
        .ReopenStepAsync(chain.Id, target, ChainActor.Harness, decision.ReviewNotes, now, ct)
        .ConfigureAwait(false);
    if (!reopened.IsSuccess)
    {
      return ChainAdvance.Refused(reopened.Error.Code, reopened.Error.Message);
    }

    // THE FIX SESSION RUNS THE TARGET'S ROLE, which is Implement in every real chain (a review
    // judges an implementer's work). Reading it from the record rather than assuming it keeps a
    // chain of reviews honest: the reopened step is started as whatever it was declared to be.
    ChainStep reopenedStep = reopened.Value.StepAt(target)!;
    return await StartReopenedAsync(reopened.Value, reopenedStep, reviewerSessionId, now, ct)
        .ConfigureAwait(false);
  }

  /// <summary>Claims and starts the reopened step. Split out so the reopen's two failure paths
  ///     (a refused claim, a refused launch) read as separate statements rather than as a
  ///     nested conditional (S3358).</summary>
  /// <param name="chain">The chain the reopened step belongs to.</param>
  /// <param name="step">The reopened step, as the record now stands.</param>
  /// <param name="predecessorSessionId">The reviewer's session, whose cell the fix takes.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<ChainAdvance> StartReopenedAsync(Chain chain, ChainStep step,
      string predecessorSessionId, DateTimeOffset now, CancellationToken ct)
  {
    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains
        .ClaimStepAsync(chain.Id, step.Position, provisional, now, ct).ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return ChainAdvance.Refused(claimed.Error.Code, claimed.Error.Message);
    }

    ChainStepAssignment assignment = new ChainStepAssignment(chain.Id, chain.PlanId,
        step.Position, null).WithRole(step.Role);
    Result<string> started = await _host
        .StartSessionAsync(assignment, predecessorSessionId, ct).ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      _ = await _chains.ReleaseStepAsync(chain.Id, step.Position, ChainActor.Harness, now, ct)
          .ConfigureAwait(false);
      return ChainAdvance.Refused(started.Error.Code, started.Error.Message);
    }

    _ = await _chains.ReassignStepAsync(chain.Id, step.Position, started.Value, now, ct)
        .ConfigureAwait(false);
    return new ChainAdvance(ChainAdvanceKind.StartStep, step.Position, null, started.Value);
  }

  /// <summary>The round-trip cap is spent: park the chain naming the step and the reason
  ///     (issue #320). A park is waiting-for-a-human, not broken (D8/D9), and the reviewer's
  ///     session is left open so a human can read what it said.</summary>
  /// <param name="chain">The chain to park.</param>
  /// <param name="decision">The cap decision, which names the step and the reason.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<ChainAdvance> ParkAtCapAsync(Chain chain, ChainReviewDecision decision,
      DateTimeOffset now, CancellationToken ct)
  {
    string reason = decision.Reason ?? "the review round-trip cap was reached.";
    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, reason, decision.Target, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return parked.IsSuccess
        ? new ChainAdvance(ChainAdvanceKind.Blocked, null, ChainReviewCode.CapReached, reason)
        : ChainAdvance.Refused(parked.Error.Code, parked.Error.Message);
  }
  /// <summary>Claims the next step for a NEW session and starts it. The claim happens
  ///     BEFORE the start so the step is unavailable to any other claimant during the launch
  ///     window; the claim is then REASSIGNED to the real id, which spends no extra attempt.
  ///     <para>WHY A PLACEHOLDER AT ALL: the chain record requires a session id to claim, and
  ///     the id does not exist until the session does.</para></summary>
  private async Task<ChainAdvance> StartStepAsync(Chain chain, int position, string predecessorSessionId,
      DateTimeOffset now, CancellationToken ct)
  {
    // THE SEPARATION RULE RUNS BEFORE THE CLAIM (issue #218): a refused review step must spend
    // no attempt and hold no claim, or it would be unavailable to the next advance while nothing
    // was ever started for it.
    Result<ChainReviewerCheck> checkedOk = await CheckReviewerAsync(chain, position, now, ct)
        .ConfigureAwait(false);
    if (!checkedOk.IsSuccess)
    {
      return ChainAdvance.Refused(checkedOk.Error.Code, checkedOk.Error.Message);
    }

    // A PARKED CHAIN IS NOT STARTED: the refusal is already on the record, so there is nothing
    // to release and nothing to report as a failure - the chain is waiting for a human.
    if (checkedOk.Value.Parked)
    {
      return new ChainAdvance(ChainAdvanceKind.Blocked, position,
          ChainAdvanceCode.ReviewerNotSeparated, checkedOk.Value.Reason);
    }

    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains.ClaimStepAsync(chain.Id, position, provisional, now, ct)
        .ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return ChainAdvance.Refused(claimed.Error.Code, claimed.Error.Message);
    }

    // THE ROLE TRAVELS FROM THE RECORD (issue #313 D7): the step's assigned role is what
    // #218's separation rule reads at session start, and the record is the only source a
    // resumed or restarted launch also has.
    ChainStepAssignment assignment = new ChainStepAssignment(chain.Id, chain.PlanId, position, null)
        .WithRole(claimed.Value.Steps.First(step => step.Position == position).Role);
    Result<string> started = await _host
        .StartSessionAsync(assignment, predecessorSessionId, ct)
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

  /// <summary>Runs the reviewer-separation rule for the step about to start (issue #218), and
  ///     records what the step that just COMPLETED served on.
  ///     <para>BOTH HALVES IN ONE PLACE, because they are one fact: the implementer's model is
  ///     recorded so a later review step can be compared against it, and a review step is
  ///     compared against what was recorded. Splitting them across two call sites would let one
  ///     be added and the other forgotten.</para>
  ///     <para>THE RECORDED MODEL IS READ FROM THE AGENT ROW, which is the durable record of what
  ///     actually served (D3). A session that never served (a launch that failed, a headless
  ///     stub) records nothing, and NULL reads as 'not recorded' rather than as a match.</para>
  ///     <para>A GATE THAT IS NOT WIRED CLEARS THE STEP. A container with no chain machinery
  ///     (headless or test hosts) behaves exactly as it did before this issue.</para></summary>
  private async Task<Result<ChainReviewerCheck>> CheckReviewerAsync(Chain chain, int position,
      DateTimeOffset now, CancellationToken ct)
  {
    if (_reviewers is null)
    {
      return Result.Success(ChainReviewerCheck.Cleared);
    }

    // THE STAMP RUNS FIRST because the gate reads the record afterwards: the implementer's model
    // must already be on the row when the comparison happens.
    await RecordPredecessorModelAsync(chain, now, ct).ConfigureAwait(false);
    return await _reviewers.CheckAsync(chain.Id, position, now, ct).ConfigureAwait(false);
  }

  /// <summary>Stamps the model the step that just completed served on (issue #218 D3), so a
  ///     review step that follows it can be compared against a RECORDED fact rather than a live
  ///     parent that has already closed.
  ///     <para>BEST EFFORT BY CONTRACT, and it does not swallow the rule: a stamp that fails
  ///     leaves the model unrecorded, which makes a later review step clear rather than refuse.
  ///     That is the honest direction - refusing a review because an unrelated write failed would
  ///     park a chain on an infrastructure fault.</para></summary>
  private async Task RecordPredecessorModelAsync(Chain chain, DateTimeOffset now, CancellationToken ct)
  {
    if (_agents is null)
    {
      return;
    }

    // EVERY step that holds a session and has no recorded model yet: the completion fact names
    // one, but a chain resumed after a restart may carry several settled sessions whose models
    // were never stamped, and a review step anywhere after them needs all of them.
    foreach (ChainStep step in chain.Steps)
    {
      if (step.SessionId is not { } sessionId || step.ServedModel is not null
          || !Guid.TryParse(sessionId, out Guid parsed))
      {
        continue;
      }

      Result<AgentRecord> record = await _agents.GetAsync(new AgentId(parsed), ct).ConfigureAwait(false);
      if (!record.IsSuccess || !IsRealModel(record.Value.ModelUsed))
      {
        continue;
      }

      _ = await _chains.RecordServedModelAsync(chain.Id, step.Position, record.Value.ModelUsed, now, ct)
          .ConfigureAwait(false);
    }
  }

  /// <summary>True when a persisted <c>ModelUsed</c> names a model that actually served. The
  ///     sentinel and the provider's bootstrap fallback both mean 'nothing was selected', and
  ///     recording either would fabricate a comparison the separation rule would then act on.
  ///     </summary>
  private static bool IsRealModel(string? modelUsed) =>
      !string.IsNullOrWhiteSpace(modelUsed) && modelUsed != AgentRecord.UnassignedModel;

  /// <summary>Parks a chain whose completion claim the gate refused (issue #304 D1).
  ///     <para>ITS OWN PARK PATH RATHER THAN <see cref="ParkBlockedAsync"/>: that one reports
  ///     <see cref="ChainAdvanceCode.StepsUnsettled"/> because a chain that cannot advance has
  ///     unsettled steps. Here nothing is unsettled - the work is done and the workspace simply
  ///     has not proved itself - so reporting 'steps unsettled' would send the reader looking for
  ///     a step that is fine. The position is the step that made the claim, so the record says
  ///     which step's completion is unbacked.</para></summary>
  private async Task<ChainAdvance> ParkForGateAsync(Chain chain, string refusal, DateTimeOffset now,
      CancellationToken ct)
  {
    int? position = chain.Steps.Count > 0 ? chain.Steps[^1].Position : null;
    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, refusal, position, ChainActor.Harness, now, ct).ConfigureAwait(false);
    return parked.IsSuccess
        ? new ChainAdvance(ChainAdvanceKind.Blocked, null, ChainAdvanceCode.CompletionGateNotFresh,
            refusal)
        : ChainAdvance.Refused(parked.Error.Code, parked.Error.Message);
  }

  /// <summary>Asks the chain-level completion gate; null means 'proceed' (issue #304 D1).
  ///     <para>A GATE THAT IS NOT WIRED CLEARS THE CHAIN, exactly as the Plan Domain's unwired
  ///     gate does: a headless host or a test container behaves as it did before this issue.
  ///     </para></summary>
  /// <param name="ct">Cancellation.</param>
  private async Task<string?> CompletionGateRefusalAsync(CancellationToken ct)
  {
    if (_completionGate is null)
    {
      return null;
    }

    (bool allowed, string message) = await _completionGate.EvaluateAsync(ct).ConfigureAwait(false);
    return allowed ? null : message;
  }

  /// <summary>Parks a chain that can no longer advance (issue #303 D3/D9): nothing is available
  ///     to start and nothing can complete, so the work is waiting for a human.
  ///     <para>THE REASON NAMES THE STEP AND WHAT BLOCKED IT, taken from the walker's own
  ///     refusal message, so a reader does not have to re-derive why the chain stopped.</para>
  ///     <para>A park is reported as a BLOCKED decision carrying the park reason: the caller
  ///     sees the same 'nothing more will happen without a human' outcome it would have seen
  ///     from the walker, now with the record to match.</para>
  ///     <para>The session is NOT closed. It is the one that reported the completion, and a
  ///     parked chain is waiting for a human who may want to read that session's transcript.</para></summary>
  private async Task<ChainAdvance> ParkBlockedAsync(Chain chain, ChainAdvance decision,
      DateTimeOffset now, CancellationToken ct)
  {
    string reason = decision.Reason ?? $"chain #{chain.Id} can no longer advance.";
    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, reason, decision.Position, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return parked.IsSuccess
        ? new ChainAdvance(ChainAdvanceKind.Blocked, null, ChainAdvanceCode.StepsUnsettled, reason)
        : ChainAdvance.Refused(parked.Error.Code, parked.Error.Message);
  }

  /// <summary>Moves a fully settled chain to Completed, with the harness as the actor (D6).
  ///     A refusal here is the chain's own consistency rule (#300's completion invariant),
  ///     reported rather than forced.
  ///     <para>THE CHAIN-LEVEL GATE RUNS FIRST (issue #304 D1), because this is where an
  ///     UNATTENDED chain claims completion: no human is watching to notice that the last step
  ///     finished on a workspace whose gate never ran. A blocking verdict PARKS the chain rather
  ///     than refusing the advance - a parked chain is waiting for a human, which is exactly
  ///     what a completion claim nobody can back is. The refusal does NOT close the session:
  ///     the person un-parking the chain will want to read the transcript that got it there.
  ///     </para>
  ///     <para>A gate that is unset, declined, or unverifiable does not block (D2, #285 D1): the
  ///     workspace has nothing to be checked against, and refusing there would demand a check the
  ///     project never wrote.</para></summary>
  private async Task<ChainAdvance> CompleteAsync(Chain chain, string sessionId, DateTimeOffset now,
      CancellationToken ct)
  {
    if (await CompletionGateRefusalAsync(ct).ConfigureAwait(false) is { } refusal)
    {
      return await ParkForGateAsync(chain, refusal, now, ct).ConfigureAwait(false);
    }

    await _host.CloseSessionAsync(sessionId, ct).ConfigureAwait(false);
    Result<Chain> completed = await _chains
        .SetStatusAsync(chain.Id, ChainStatus.Completed, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return completed.IsSuccess
        ? new ChainAdvance(ChainAdvanceKind.CompleteChain, null, null, null)
        : ChainAdvance.Refused(completed.Error.Code, completed.Error.Message);
  }
}

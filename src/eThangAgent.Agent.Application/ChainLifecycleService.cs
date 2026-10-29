using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>What the harness needs from the shell to handle a step failure (issue #303 D1/D3).
///     <para>WHY A PORT: resuming a session is a tab operation - a session is a cell, a
///     container, and a host process - and only the shell owns those. The decision belongs to
///     the Agent Domain, so this is the seam between them and the service stays testable with
///     a fake.</para>
///     <para>DISTINCT FROM <see cref="IChainAdvanceHost"/>: that one CLOSES a finished session
///     and STARTS a fresh one for the next step. This one RE-ENTERS a session that already
///     exists, which is the whole point of D1 - a retry resumes, it does not restart.</para></summary>
public interface IChainLifecycleHost
{
  /// <summary>Re-enters <paramref name="sessionId"/> with <paramref name="message"/>, keeping
  ///     its transcript. False when the session is unreachable (closed, or its host is gone),
  ///     so a retry that never landed does not spend the attempt budget.</summary>
  /// <param name="sessionId">The session to resume.</param>
  /// <param name="message">The failure note the session is re-entered with (D7).</param>
  /// <param name="ct">Cancellation.</param>
  Task<bool> TryResumeAsync(string sessionId, string message, CancellationToken ct = default);
}

/// <summary>What a step failure decided (issue #303 D1/D2/D3).</summary>
/// <param name="Decision">Retry the same session, or park the chain.</param>
/// <param name="Position">The step that failed, or null when the chain holds no step for the
///     session (an ordinary session, or one whose step was already settled).</param>
/// <param name="Reason">Why the chain parked, or null when it retried.</param>
public sealed record ChainFailureOutcome(ChainRetryDecision Decision, int? Position, string? Reason)
{
  /// <summary>True when the chain was parked waiting for a human.</summary>
  public bool Parked => Decision == ChainRetryDecision.Park;

  /// <summary>True when the same session was resumed for another attempt.</summary>
  public bool Retried => Decision == ChainRetryDecision.Retry;
}

/// <summary>Handles a chain step's session failing (issue #303): resume the same session while
///     the attempt budget allows, park the chain when it does not.
///     <para>THE RULE IS <see cref="ChainRetryPolicy"/>; this class only performs what the rule
///     decided, so there is exactly one place the rule lives and one place the side effects
///     live - the <see cref="ChainAdvanceService"/> shape, for the same reason.</para>
///     <para>ORDER IS LOAD-BEARING. The step is re-entered BEFORE the session is resumed, so the
///     record already shows a live claim when the session starts working; a resumed session that
///     completed instantly would otherwise find its own step still Failed and be refused by the
///     ownership gate.</para>
///     <para>AN UNREACHABLE SESSION SPENDS NO ATTEMPT (the <see cref="RootSessionWatchdog"/>
///     rule): a resume that never landed did not fail the work, and burning the budget on it
///     would park a chain a human never got the chance to unblock.</para></summary>
public sealed class ChainLifecycleService(ChainService chains, IChainLifecycleHost host)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainLifecycleHost _host = host ?? throw new ArgumentNullException(nameof(host));

  /// <summary>Records that the session's step failed, then retries or parks.
  ///     <para>THE POSITION COMES FROM THE RECORD, never from the caller: the failure fact
  ///     carries a session id, and the chain already knows which step that session holds. A
  ///     caller-supplied position could disagree with the assignment.</para></summary>
  /// <param name="sessionId">The session whose step failed.</param>
  /// <param name="failure">What the failed attempt recorded, when it recorded anything.</param>
  /// <param name="now">The clock the claim window and the retry are measured against.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainFailureOutcome>> OnStepFailedAsync(string sessionId, string? failure,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    Result<Chain?> found = await _chains.FindBySessionAsync(sessionId, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      return Result.Failure<ChainFailureOutcome>(found.Error);
    }

    // An ordinary session, or one whose step was already settled: there is nothing to retry.
    // Not an error - the same answer the advance gives for a session in no chain.
    if (found.Value is not { } chain)
    {
      return Result.Success(new ChainFailureOutcome(ChainRetryDecision.Park, null,
          $"session {sessionId} is not assigned to any chain step, so there is nothing to retry."));
    }

    ChainStep? step = chain.Steps.FirstOrDefault(s =>
        string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
    return step is null
        ? Result.Success(new ChainFailureOutcome(ChainRetryDecision.Park, null,
            $"chain #{chain.Id} holds no step for session {sessionId}; there is nothing to retry."))
        : await FailAsync(chain, step, failure, now, ct).ConfigureAwait(false);
  }

  /// <summary>Settles the step Failed, then retries or parks. Extracted so a caller that
  ///     already holds the chain drives the same path.</summary>
  private async Task<Result<ChainFailureOutcome>> FailAsync(Chain chain, ChainStep step,
      string? failure, DateTimeOffset now, CancellationToken ct)
  {
    Result<Chain> settled = await _chains
        .SettleStepAsync(chain.Id, step.Position, ChainStepStatus.Failed,
            ChainActor.Session(step.SessionId!), failure, now, ct)
        .ConfigureAwait(false);
    if (!settled.IsSuccess)
    {
      return Result.Failure<ChainFailureOutcome>(settled.Error);
    }

    ChainStep failed = settled.Value.Steps.First(s => s.Position == step.Position);
    return ChainRetryPolicy.Decide(failed.Attempts) == ChainRetryDecision.Park
      ? Result.Success(await ParkAsync(settled.Value, failed, now, ct).ConfigureAwait(false))
      : Result.Success(await RetryAsync(settled.Value, failed, now, ct).ConfigureAwait(false));
  }

  /// <summary>Resumes the same session with the failure note (D1 + D7).
  ///     <para>A resume that does not land leaves the step FAILED and reports the park reason:
  ///     the session is gone, so there is no transcript to continue and no attempt to spend.</para></summary>
  private async Task<ChainFailureOutcome> RetryAsync(Chain chain, ChainStep step, DateTimeOffset now,
      CancellationToken ct)
  {
    Result<Chain> retried = await _chains
        .RetryStepAsync(chain.Id, step.Position, step.SessionId!, now, ct)
        .ConfigureAwait(false);
    if (!retried.IsSuccess)
    {
      return new ChainFailureOutcome(ChainRetryDecision.Park, step.Position,
          $"chain #{chain.Id} step {step.Position} could not be re-entered: {retried.Error.Message}");
    }

    string note = ChainRetryPolicy.FailureNote(chain.Id, step.Position, step.Attempts, step.Outcome);
    if (await _host.TryResumeAsync(step.SessionId!, note, ct).ConfigureAwait(false))
    {
      return new ChainFailureOutcome(ChainRetryDecision.Retry, step.Position, null);
    }

    // THE RESUME DID NOT LAND: the session is gone, so there is no transcript to continue and
    // nothing to retry. Park the chain and say so - a Running chain with an unreachable session
    // would sit forever while every surface reported it as in progress, and the step's claim
    // would silently lapse into 'available' for a session that does not exist.
    string unreachable = $"chain #{chain.Id} step {step.Position} failed and its session {step.SessionId} is unreachable, "
        + "so the work cannot be resumed. Re-open that session from the Sessions menu, or start the step again.";
    _ = await _chains.ParkAsync(chain.Id, unreachable, step.Position, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return new ChainFailureOutcome(ChainRetryDecision.Park, step.Position, unreachable);
  }

  /// <summary>Parks the chain: the attempt budget is spent, so the work is waiting for a human
  ///     rather than broken (D3). The reason names the step, the attempt count, and the failure.</summary>
  private async Task<ChainFailureOutcome> ParkAsync(Chain chain, ChainStep step, DateTimeOffset now,
      CancellationToken ct)
  {
    string reason = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"chain #{chain.Id} step {step.Position} failed {step.Attempts} time(s), reaching the {ChainRetryPolicy.MaxAttempts}-attempt cap");
    reason = string.IsNullOrWhiteSpace(step.Outcome)
        ? reason + "."
        : reason + ": " + step.Outcome.Trim();
    _ = await _chains.ParkAsync(chain.Id, reason, step.Position, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return new ChainFailureOutcome(ChainRetryDecision.Park, step.Position, reason);
  }
}

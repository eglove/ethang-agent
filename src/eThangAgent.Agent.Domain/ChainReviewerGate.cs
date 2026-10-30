using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>What the reviewer-separation gate decided (issue #218).</summary>
/// <param name="Parked">True when the refusal parked the chain: a match is a CONFIGURATION
///     defect the record must carry, so it parks rather than aborting (the parent's D8).</param>
/// <param name="Reason">The park reason, or null when the step may start.</param>
public sealed record ChainReviewerCheck(bool Parked, string? Reason)
{
  /// <summary>The step may start.</summary>
  public static ChainReviewerCheck Cleared { get; } = new(Parked: false, Reason: null);
}

/// <summary>Enacts <see cref="ReviewerSeparationPolicy"/> (issue #218): the ONE place a review
///     step's model is checked before its session starts, called by every launch site.
///     <para>WHY A GATE AND NOT A CHECK INSIDE THE AGGREGATE: the rule has TWO answers, and they
///     are not interchangeable. A MISSING model is a DECLARATION defect - nothing can be
///     compared, so nothing is claimed and the caller reports the typed error. A MATCH is a
///     CONFIGURATION defect on a record that already exists, so it PARKS the chain (B5, parent
///     D8): the work is waiting for a human, not broken, and a chain that stayed Running would
///     sit forever while every surface reported it in progress.</para>
///     <para>IT PARKS THROUGH <see cref="ChainService"/> so the refusal lands on the record with
///     the harness as its actor (parent D11) - the same write path #303's lifecycle uses for a
///     spent attempt budget.</para>
///     <para>THE POLICY IS PURE AND THIS IS NOT, deliberately: the rule is unit-testable with no
///     store, and the side effect lives in exactly one place instead of at each launch site.</para>
///     </summary>
public sealed class ChainReviewerGate(ChainService chains)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  /// <summary>Checks the step at <paramref name="position"/> before anything is claimed or
  ///     started.
  ///     <para>ORDER IS LOAD-BEARING. This runs BEFORE the claim: a refused review step must
  ///     spend no attempt and hold no claim, or the step would be unavailable to the next
  ///     advance while nothing was ever started for it.</para>
  ///     <para>THE GATE LOADS THE RECORD ITSELF rather than trusting the caller's snapshot, and
  ///     that is a correctness requirement rather than a convenience. The implementer's served
  ///     model is stamped onto the chain moments before this runs, which moves the version - so a
  ///     caller holding the pre-stamp snapshot would compare a reviewer against an EMPTY
  ///     implementer model and CLEAR a step the rule should have refused. Reading here makes the
  ///     staleness unrepresentable instead of a rule every caller has to remember.</para></summary>
  /// <param name="chainId">The chain to check.</param>
  /// <param name="position">The 1-based step position about to start.</param>
  /// <param name="now">The clock the park is recorded against.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainReviewerCheck>> CheckAsync(int chainId, int position,
      DateTimeOffset now, CancellationToken ct = default)
  {
    Result<Chain> loaded = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      // AN UNREADABLE RECORD REFUSES RATHER THAN CLEARS: starting a reviewer without being able
      // to check separation is exactly the outcome the rule exists to prevent, and the caller
      // reports the store's own error.
      return Result.Failure<ChainReviewerCheck>(loaded.Error);
    }

    Chain chain = loaded.Value;
    if (chain.StepAt(position) is not { } step)
    {
      // An unknown position is the caller's problem and the aggregate already refuses it by
      // name; the separation rule has nothing to say about a step that does not exist.
      return Result.Success(ChainReviewerCheck.Cleared);
    }

    return ReviewerSeparationPolicy.MissingModel(step.Role, position) is { } missing
        ? Result.Failure<ChainReviewerCheck>(missing)
        : await NotSeparatedAsync(chain, step, now, ct).ConfigureAwait(false);
  }

  /// <summary>The separation half: park on a match, clear otherwise.
  ///     <para>A park that cannot be written is a FAILURE, not a clear: reporting 'the step may
  ///     start' when the refusal never reached the record would start a reviewer the rule had
  ///     just refused.</para></summary>
  private async Task<Result<ChainReviewerCheck>> NotSeparatedAsync(Chain chain, ChainStep step,
      DateTimeOffset now, CancellationToken ct)
  {
    if (ReviewerSeparationPolicy.NotSeparated(chain, step) is not { } match)
    {
      return Result.Success(ChainReviewerCheck.Cleared);
    }

    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, match.Message, step.Position, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return parked.IsSuccess
        ? Result.Success(new ChainReviewerCheck(Parked: true, Reason: match.Message))
        : Result.Failure<ChainReviewerCheck>(parked.Error);
  }
}

namespace eThangAgent.AgentDomain;

/// <summary>What opening a chain step's session should do about the claim it finds.</summary>
public enum ChainClaimAdoptionKind
{
  /// <summary>The claim is already this session's, or the chain is frozen: write nothing.</summary>
  Nothing,

  /// <summary>The claim is a placeholder: correct it to the session that opened.</summary>
  Adopt,

  /// <summary>The claim belongs to another real session, or names no step: leave it alone.</summary>
  Refuse,
}

/// <summary>What the policy decided, and why when it refused.</summary>
/// <param name="Kind">The act to take.</param>
/// <param name="Reason">Why nothing was done, or null when the kind is Adopt.</param>
public sealed record ChainClaimAdoption(ChainClaimAdoptionKind Kind, string? Reason = null)
{
  /// <summary>The decision to correct a placeholder claim.</summary>
  public static ChainClaimAdoption Adopt() => new(ChainClaimAdoptionKind.Adopt);

  /// <summary>The decision to write nothing, with the reason it was asked at all.</summary>
  /// <param name="reason">Why no write is warranted.</param>
  public static ChainClaimAdoption Nothing(string? reason = null)
      => new(ChainClaimAdoptionKind.Nothing, reason);

  /// <summary>The decision to leave the claim where it is.</summary>
  /// <param name="reason">Why the claim cannot be adopted.</param>
  public static ChainClaimAdoption Refuse(string reason)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    return new ChainClaimAdoption(ChainClaimAdoptionKind.Refuse, reason);
  }
}

/// <summary>Decides whether a session opening on a chain step should repair the claim it finds,
///     as a PURE function over the chain record.
///     <para>THE HARNESS CLAIMS BEFORE IT CAN KNOW THE SESSION'S ID, so every launch writes the
///     claim twice. When the second write is lost, the step is held by a placeholder and the
///     session that did the work cannot complete it. Opening is the moment that can be
///     repaired, because the session's own id is the fact the placeholder stood in for.</para>
///     <para>A REAL CLAIMANT IS AN OWNER and is never displaced. Adoption corrects a guess; it
///     is not a way for one session to take another's work.</para></summary>
public static class ChainClaimAdoptionPolicy
{
  /// <summary>The refusal for a claim held by another real session, named so a reader knows
  ///     which session owns the work.
  ///     <para>EXTRACTED FOR COMPLEXITY (S1541): the decision is three questions and a nested
  ///     conditional, and folding the message into it pushed the method past the bound.</para></summary>
  /// <param name="chain">The chain the step belongs to.</param>
  /// <param name="step">The step whose claim is not adoptable.</param>
  private static ChainClaimAdoption ForeignClaim(Chain chain, ChainStep step)
    => ChainClaimAdoption.Refuse(
        $"chain #{chain.Id} step {step.Position} is held by session {step.SessionId ?? "(none recorded)"} "
        + "and only a placeholder claim can be adopted.");

  /// <summary>What to do about the claim on <paramref name="position"/> for
  ///     <paramref name="sessionId"/>.</summary>
  /// <param name="chain">The chain the session belongs to.</param>
  /// <param name="position">The step the session was started for.</param>
  /// <param name="sessionId">The session that is opening.</param>
  public static ChainClaimAdoption Decide(Chain chain, int position, string sessionId)
  {
    ArgumentNullException.ThrowIfNull(chain);
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    // A frozen chain takes no writes, and a repair is not an exception to that.
    return chain.Status is ChainStatus.Completed or ChainStatus.Abandoned
      ? ChainClaimAdoption.Nothing($"chain #{chain.Id} is {chain.Status} and takes no writes.")
      : ClaimFor(chain, position, sessionId);
  }

  /// <summary>The decision for a chain that can still be written: read its step, and either
  ///     adopt the claim or say why not.</summary>
  /// <param name="chain">The chain the session belongs to.</param>
  /// <param name="position">The step the session was started for.</param>
  /// <param name="sessionId">The session that is opening.</param>
  private static ChainClaimAdoption ClaimFor(Chain chain, int position, string sessionId)
  {
    return chain.StepAt(position) is { } step
      ? AlreadyHeld(step, position, sessionId) ?? ClaimDecision(chain, step)
      : ChainClaimAdoption.Refuse(
          $"chain #{chain.Id} has no step {position}, so there is no claim to adopt.");
  }

  /// <summary>The decision for a step this session does not already hold: adopt a placeholder,
  ///     refuse another session's real claim.
  ///     <para>EXTRACTED SO <see cref="Decide"/> READS AS THE THREE QUESTIONS IT ASKS, rather than
  ///     as a nested conditional the reader has to unwind (S1541).</para></summary>
  /// <param name="chain">The chain the step belongs to.</param>
  /// <param name="step">The step whose claim is being read.</param>
  private static ChainClaimAdoption ClaimDecision(Chain chain, ChainStep step)
    => step.HoldsPlaceholderClaim ? ChainClaimAdoption.Adopt() : ForeignClaim(chain, step);

  /// <summary>The 'nothing to do' answer for a step this session already holds, or null when it
  ///     does not hold it.
  ///     <para>RETURNED AS A VALUE rather than branched on, because a caller that wrote this as a
  ///     guard reads the decision as two steps when it is one.</para></summary>
  /// <param name="step">The step whose claim is being read.</param>
  /// <param name="position">The step position, for the message.</param>
  /// <param name="sessionId">The session that is opening.</param>
  private static ChainClaimAdoption? AlreadyHeld(ChainStep step, int position, string sessionId)
    => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)
      ? ChainClaimAdoption.Nothing($"step {position} is already held by this session.")
      : null;
}

namespace eThangAgent.AgentDomain;

/// <summary>The named refusals a resume produces. Each is a different answer a caller acts on
///     differently, so collapsing them would tell a user 'not parked' when the chain does not
///     exist.</summary>
public static class ChainResumeCode
{
  /// <summary>The chain exists but is not Parked, so there is nothing to resume. A running
  ///     chain is already moving and a finished one is frozen.</summary>
  public const string NotParked = "ChainNotParked";
}

/// <summary>What a resume did, so a caller can report it rather than infer it from the record.
///     <para>THE THREE FACTS ARE SEPARATE ON PURPOSE. A resume un-parks the chain, may re-arm
///     the step that stopped it, and may start a fresh session for a step - and each of those
///     can be the whole answer: a chain parked for a review denial needs no re-arm, while one
///     parked on a spent attempt budget needs both.</para></summary>
/// <param name="ChainId">The chain that was resumed.</param>
/// <param name="RearmedPosition">The step put back to Pending, or null when the park named no
///     step or the step was already available.</param>
/// <param name="StartedPosition">The step a new session was started for, or null when the
///     advance found nothing to start.</param>
/// <param name="SessionId">The new session's id, or null when nothing was started.</param>
public sealed record ChainResumeOutcome(int ChainId, int? RearmedPosition, int? StartedPosition,
    string? SessionId);

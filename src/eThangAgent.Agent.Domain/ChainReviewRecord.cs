namespace eThangAgent.AgentDomain;

/// <summary>What an implementation review concluded.
///     <para>TWO VERDICTS, NOT THREE. A review of IMPLEMENTATION answers one question - may this
///     work stand? - so the vocabulary is pass or fail. A plan review's approve-with-conditions
///     has no counterpart here: conditions on built work would be findings the fix session
///     addresses, which is what a fail already means.</para>
///     <para>DISTINCT FROM THE PLAN REVIEW. <c>PlanReviewVerdict</c> judges a plan VERSION before
///     anything is built; this judges work that was built. They are different artifacts and
///     different moments, so they are different records with different verdicts.</para></summary>
public enum ChainReviewVerdict
{
  /// <summary>The work may stand: the chain advances.</summary>
  Pass,

  /// <summary>The work goes back for a fix: the judged step reopens with these findings.
  ///     </summary>
  Fail,
}

/// <summary>One cited finding.
///     <para>THE CITATION IS CARRIED, NOT SUMMARISED. The citation is what makes the note
///     actionable, and a summary is a second lossy step between the reviewer and the fix
///     session: the fixer would be acting on someone's paraphrase of the reviewer rather than on
///     the reviewer.</para>
///     <para>A CITATION NAMES WHAT THE FINDING IS ABOUT: a requirement id, a file path, a test
///     name, or a step position. A finding with no citation is a complaint, so the write gate
///     refuses it.</para></summary>
/// <param name="Text">What the reviewer observed.</param>
/// <param name="Citation">The requirement, file, test, or step position it is about.</param>
public sealed record ChainReviewFinding(string Text, string Citation)
{
  /// <summary>True when this finding says something AND cites something - the same predicate
  ///     the record enforces at write time, so a malformed row is caught before a brief renders
  ///     it as though it were actionable.</summary>
  public bool IsWellFormed =>
      !string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(Citation);
}

/// <summary>The review a chain step's session recorded: the verdict and the cited findings the
///     cursor and the fix brief both read.
///     <para>ONE READ, TWO READERS. The cursor decides advance / back-edge / park from
///     <see cref="Verdict"/>; the fix brief renders <see cref="Findings"/>. Splitting them into
///     two reads would let the cursor act on a verdict whose findings the brief never saw.
///     </para>
///     <para>A FAIL WITH NO FINDINGS IS REFUSED BY THE POLICY, not here: the record states what
///     the reviewer said, and the rule about what makes a denial actionable belongs to the
///     decision that acts on it.</para></summary>
/// <param name="Verdict">Pass or fail.</param>
/// <param name="Findings">What the reviewer found, each cited.</param>
/// <param name="ReviewerModel">The model the reviewer ran on, when recorded.</param>
public sealed record ChainReviewRecord(ChainReviewVerdict Verdict,
    IReadOnlyList<ChainReviewFinding> Findings, string? ReviewerModel = null)
{
  /// <summary>True when the reviewer said no: the one verdict that moves the cursor BACK.
  ///     </summary>
  public bool IsDenial => Verdict == ChainReviewVerdict.Fail;

  /// <summary>True when this review carries something a fixer can act on. A denial with no
  ///     findings is refused rather than delivered: a session told 'the review failed' with no
  ///     notes will guess, and guessing is what the rule exists to prevent.</summary>
  public bool HasFindings => Findings.Count > 0;
}

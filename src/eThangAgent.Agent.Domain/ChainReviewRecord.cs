namespace eThangAgent.AgentDomain;

/// <summary>What a reviewer concluded, as the CHAIN CURSOR reads it (issue #320).
///     <para>WHY A SECOND SPELLING OF THE VERDICT: the record lives in the Plan Domain
///     (<c>PlanReviewVerdict</c>) and the Agent Domain does not reference it — the same
///     boundary that makes <see cref="ChainBriefPlanStep"/> a mirror of <c>PlanStep</c>
///     and <see cref="ChainPlanFacts"/> a mirror of the plan's own shape. The composition
///     root maps one onto the other in ONE place, and a test pins that mapping and these
///     tokens against <c>PlanReviewVerdictCodec</c> so the two cannot drift apart.</para>
///     <para>THE MEMBERS ARE THE RECORD'S MEMBERS, not a subset: the cursor needs
///     approve-with-conditions to be distinguishable from a plain approve, because the
///     conditions are findings the planner must address (issue #219's surviving rule).
///     </para></summary>
public enum ChainReviewVerdict
{
  /// <summary>The work may advance.</summary>
  Approve,

  /// <summary>The work goes back for a fix.</summary>
  Deny,

  /// <summary>The work may advance once the cited findings are addressed.</summary>
  ApproveWithConditions,
}

/// <summary>One cited finding, as the chain reads it (issue #320).
///     <para>THE CITATION IS CARRIED, NOT SUMMARISED. Issue #219's surviving rule: the fix
///     brief carries the findings WITH their citations, because the citation is what makes
///     the note actionable and a summary is a second lossy step between the reviewer and the
///     planner.</para>
///     <para>Mirrors <c>PlanReviewFinding</c> across the domain boundary for the reason
///     stated on <see cref="ChainReviewVerdict"/>.</para></summary>
/// <param name="Text">What the reviewer observed.</param>
/// <param name="Citation">The requirement id or step position it is about.</param>
public sealed record ChainReviewFinding(string Text, string Citation)
{
  /// <summary>True when this finding says something AND cites something — the same predicate
  ///     the record enforces at write time, so a malformed row is caught before a brief renders
  ///     it as though it were actionable.</summary>
  public bool IsWellFormed =>
      !string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(Citation);
}

/// <summary>The review a chain step's session recorded (issue #320): the verdict and the
///     cited findings the cursor and the fix brief both read.
///     <para>ONE READ, TWO READERS. The cursor decides advance / back-edge / park from
///     <see cref="Verdict"/>; the fix brief renders <see cref="Findings"/>. Splitting them
///     into two reads would let the cursor act on a verdict whose findings the brief never
///     saw.</para>
///     <para><paramref name="ReviewerModel"/> rides along because a verdict's provenance is
///     part of the verdict (issue #216 D6), and the chain record is what a later reader has.
///     </para></summary>
/// <param name="Verdict">What the reviewer concluded.</param>
/// <param name="Findings">The cited findings, in the order the reviewer wrote them.</param>
/// <param name="Scores">The three rubric dimensions the reviewer scored (issue #216 D3). They
///     ride the record because a review row must round-trip with its scores: a review without
///     them cannot be compared with any other, which is the whole reason the dimensions are
///     named rather than free text. Defaults to all-zero for a caller that scores nothing - the
///     store enforces only non-negativity, and a fabricated mid-scale value would be a number
///     no reviewer wrote.</param>
/// <param name="ReviewerModel">The model the reviewer ran on, when recorded.</param>
public sealed record ChainReviewRecord(ChainReviewVerdict Verdict,
    IReadOnlyList<ChainReviewFinding> Findings, ChainReviewScores? Scores = null,
    string? ReviewerModel = null)
{

  /// <summary>The three rubric dimensions, never null: an absent set reads as all-zero, the
  ///     same way an absent role reads as implement. The scale is the rubric's (#217), so this
  ///     record carries the numbers and judges nothing about them.</summary>
  public ChainReviewScores EffectiveScores => Scores ?? ChainReviewScores.Unscored;
  /// <summary>True when the reviewer said no: the one verdict that moves the cursor BACK.
  ///     <para>Approve-with-conditions is NOT a denial. It lets the chain advance and carries
  ///     its conditions into the next brief, which is the distinction #216's verdict type
  ///     exists to express.</para></summary>
  public bool IsDenial => Verdict == ChainReviewVerdict.Deny;

  /// <summary>True when this review carries something a planner can act on. A denial with
  ///     no findings is refused rather than delivered (issue #219's rule): a session told
  ///     'the reviewer denied' with no notes will guess, and guessing is what the rule
  ///     exists to prevent.</summary>
  public bool HasFindings => Findings.Count > 0;
}

/// <summary>The three rubric dimensions a review scores (issue #216 D3), mirrored across the
///     domain boundary for the reason stated on <see cref="ChainReviewVerdict"/>.
///     <para>NAMED DIMENSIONS, NOT A FREE-TEXT SCORE: named dimensions compare across reviews
///     and trend, which free text cannot. Only non-negativity is enforced here, because the
///     SCALE belongs to the <c>reviewing-plans</c> rubric (#217) and pinning a range in the
///     record would make the record the authority on a rule it does not own.</para></summary>
/// <param name="RequirementCoverage">How completely the plan covers its requirements.</param>
/// <param name="Testability">How checkable the plan's steps are.</param>
/// <param name="BlastRadius">How much of the workspace the plan touches.</param>
public sealed record ChainReviewScores(int RequirementCoverage, int Testability, int BlastRadius)
{
  /// <summary>All-zero: what a review that recorded no scores says. A real 0 and an unrecorded
  ///     score are the same value here, which is why the record states them rather than
  ///     inferring a mid-scale number.</summary>
  public static ChainReviewScores Unscored { get; } = new(0, 0, 0);

  /// <summary>True when every dimension is non-negative - the predicate the write gate reads.
  ///     </summary>
  public bool IsWellFormed =>
      RequirementCoverage >= 0 && Testability >= 0 && BlastRadius >= 0;
}

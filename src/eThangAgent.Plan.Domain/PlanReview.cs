using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>A persisted plan review (issue #216): which plan, which version it judged, the
///     verdict, the rubric scores, the cited findings, who reviewed, and when.
///     <para>KEYED ON PLAN ID PLUS THE JUDGED VERSION (D1). A verdict about version 3 must
///     stay distinguishable from a verdict about version 7 after the planner revises,
///     otherwise a stale approve silently covers content nobody reviewed. The plan already
///     carries its version, so the key needs no new state on the aggregate.</para>
///     <para>THE REVIEWER IS A SESSION (D6), not an agent: under #299 every agent IS a
///     session, and a session id is what makes the review resumable and inspectable through
///     the Sessions catalog. The model id is recorded beside it because the same session can
///     be resumed on a different model, and a verdict's provenance is part of the verdict.</para>
///     <para>ONE RECORD SERVES TWO READERS (D7): the chain cursor deciding whether to advance,
///     and the telemetry report (#309) computing calibration. They read the same rows through
///     the same port, so a reported number and an advance decision cannot disagree.</para>
///     <para>APPEND-ONLY. A review is a historical fact - there is no update operation, and
///     revising a verdict would mean recording a new review of the version as it now
///     stands.</para>
///     <para>VALIDATION IS A METHOD, NOT A CONSTRUCTOR, and the reason is mechanical: the
///     store rehydrates this record from a row it already trusts, so construction must not be
///     able to fail, while a WRITE must. <see cref="Validate"/> is the write-time gate and
///     the store calls it before inserting - which is what makes 'rejected at write time'
///     true of a record a caller assembled by hand rather than through <see cref="New"/>.</para>
///     <para>THE TWO PROVENANCE FIELDS ARE NULLABLE ON THE RECORD because a caller can
///     construct one without them and the gate must be able to SAY SO rather than the
///     constructor throwing; a stored row always carries both, and the store's reader
///     supplies them non-null.</para></summary>
/// <param name="Id">The store-assigned id; 0 on a record that has not been saved.</param>
/// <param name="PlanId">The plan that was judged.</param>
/// <param name="PlanVersion">The exact plan version the reviewer saw (D1).</param>
/// <param name="Verdict">What the reviewer concluded.</param>
/// <param name="ReviewerSessionId">The session that produced the verdict (D6).</param>
/// <param name="ReviewerModel">The model that session ran on (D6).</param>
/// <param name="Scores">The three rubric dimensions (D3).</param>
/// <param name="Findings">The cited findings (D4); empty is legal for a clean approve.</param>
/// <param name="Notes">Optional free-text context from the reviewer.</param>
/// <param name="CreatedAt">When the review was recorded.</param>
public sealed record PlanReview(
    int Id,
    int PlanId,
    int PlanVersion,
    PlanReviewVerdict Verdict,
    string? ReviewerSessionId,
    string? ReviewerModel,
    PlanReviewScores Scores,
    IReadOnlyList<PlanReviewFinding> Findings,
    string? Notes,
    DateTimeOffset CreatedAt)
{
  /// <summary>Builds an UNSAVED review (id 0) in the shape a reviewer produces.
  ///     <para>This factory does NOT validate: <see cref="Validate"/> is the single gate,
  ///     called by the store at write time. Two gates would be two copies of one rule, and
  ///     the one that matters is the one every write goes through - including a record a
  ///     caller assembled directly.</para>
  ///     <para>THE ARGUMENT IS A SHAPE, NOT A PARAMETER LIST. Ten positional parameters
  ///     would be a call site nobody can read and an analyzer refusal (S107); the caller
  ///     already has a <see cref="PlanReview"/>-shaped value, so it passes one.</para></summary>
  /// <param name="draft">The review as the caller assembled it; its Id is ignored.</param>
  public static PlanReview New(PlanReview draft)
  {
    ArgumentNullException.ThrowIfNull(draft);
    return draft with { Id = 0, Findings = [.. draft.Findings ?? []] };
  }

  /// <summary>True when this verdict lets the work advance: approve, or
  ///     approve-with-conditions.
  ///     <para>THE CONDITIONS STILL HAVE TO BE ADDRESSED - that is #219's delivery and #320's
  ///     cursor. What this answers is the narrower question the record can answer on its own:
  ///     did the reviewer say no?</para></summary>
  public bool IsApproval =>
      Verdict is PlanReviewVerdict.Approve or PlanReviewVerdict.ApproveWithConditions;

  /// <summary>The write-time gate (issue #216 B5): every required field is present, the
  ///     verdict is a defined member, the scores are non-negative, and every finding cites
  ///     something. Returns the first refusal as a typed <see cref="DomainError"/> so the
  ///     store can hand it back through <see cref="Result{T}"/> without throwing.
  ///     <para>FINDINGS ARE CHECKED ONE BY ONE and the refusal names the offender's index,
  ///     so a caller with twelve findings is not told only that 'something' was uncited.</para>
  ///     <para>THE GUARDS ARE SEPARATE METHODS rather than one expression: each answers a
  ///     different question, and a single chained conditional would be unreadable at this
  ///     length (and S3358 refuses it outright).</para></summary>
  public Result<PlanReview> Validate() =>
      RefusalFor() is { } refusal ? Result.Failure<PlanReview>(refusal) : Result.Success(this);

  /// <summary>The first reason this review cannot be written, or null when it can, in the
  ///     order the rules are stated.</summary>
  private DomainError? RefusalFor() => IdRefusal()
      ?? VerdictRefusal()
      ?? ProvenanceRefusal()
      ?? ScoreRefusal()
      ?? FindingRefusal();

  private DomainError? IdRefusal() => PlanId <= 0 ? PlanRefusal() : VersionRefusal();

  private DomainError PlanRefusal() => Refuse("PlanNotFound",
      $"a review must name the plan it judged; {PlanId} is not a plan id.");

  private DomainError? VersionRefusal() => PlanVersion <= 0
      ? Refuse("InvalidPlanVersion",
          $"a review must name the plan version it judged; {PlanVersion} is not a version.")
      : null;

  private DomainError? VerdictRefusal() => PlanReviewVerdictCodec.IsDefined(Verdict)
      ? null
      : Refuse("UnknownVerdict",
          $"'{(int)Verdict}' is not a plan review verdict; the allowed values are "
          + string.Join(", ", PlanReviewVerdictCodec.Tokens) + ".");

  private DomainError? ProvenanceRefusal() =>
      string.IsNullOrWhiteSpace(ReviewerSessionId) ? SessionRefusal() : ModelRefusal();

  private static DomainError SessionRefusal() => Refuse("MissingReviewerSession",
      "a review must name the session that produced it, or its verdict has no provenance.");

  private DomainError? ModelRefusal() => string.IsNullOrWhiteSpace(ReviewerModel)
      ? Refuse("MissingReviewerModel",
          "a review must name the model the reviewer ran on, or its verdict has no provenance.")
      : null;

  private DomainError? ScoreRefusal() => PlanReviewScores.RefusalFor(
      Scores.RequirementCoverage, Scores.Testability, Scores.BlastRadius);

  private DomainError? FindingRefusal()
  {
    for (int i = 0; i < Findings.Count; i++)
    {
      if (!Findings[i].IsWellFormed)
      {
        return Refuse("UncitedFinding", $"finding {i + 1}: " + Describe(Findings[i]));
      }
    }

    return null;
  }

  private static string Describe(PlanReviewFinding finding) =>
      string.IsNullOrWhiteSpace(finding.Text)
          ? "the finding says nothing."
          : PlanReviewFinding.MissingCitation(finding.Text).Message;

  private static DomainError Refuse(string code, string message) => new(code, message);
}

using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>One cited finding from a review (issue #216 D4).
///     <para>A FINDING THAT CITES NOTHING IS NOT A FINDING. 'the code is unclear' cannot
///     be acted on or audited, while 'step 3 has no acceptance criterion for requirement
///     R2' can. The citation is what makes the note actionable, so it is REQUIRED -
///     checked by <see cref="Create"/> and again by <see cref="PlanReview.Validate"/> at
///     write time, because a default would let an uncited finding into the record and make
///     the rule unenforceable afterwards.</para>
///     <para>THE CITATION IS NOT PARSED. It names a requirement id or a step position, and
///     which of the two it is belongs to the plan it cites, not to this record: a parser
///     here would be a second, weaker model of the plan's own numbering.</para>
///     <para>A NULL ARGUMENT IS A BLANK ONE, not a programmer error. The realistic caller
///     is a model filling a JSON payload, so a missing key arrives as null and must earn
///     the same typed refusal an empty string does rather than an exception.</para></summary>
/// <param name="Text">What the reviewer observed.</param>
/// <param name="Citation">The requirement id or step position the finding is about.</param>
public sealed record PlanReviewFinding(string Text, string Citation)
{
  /// <summary>Builds a finding, refusing a blank text or a blank citation with a typed
  ///     <c>UncitedFinding</c> error naming which half was missing.</summary>
  /// <param name="text">What the reviewer observed.</param>
  /// <param name="citation">The requirement id or step position it is about.</param>
  public static Result<PlanReviewFinding> Create(string? text, string? citation)
  {
    string said = text ?? string.Empty;
    string cites = citation ?? string.Empty;
    return RefusalFor(said, cites) is { } refusal
        ? Result.Failure<PlanReviewFinding>(refusal)
        : Result.Success(new PlanReviewFinding(said.Trim(), cites.Trim()));
  }

  /// <summary>True when this finding says something AND cites something. The single
  ///     predicate <see cref="Create"/> and <see cref="PlanReview.Validate"/> both read, so
  ///     the rule has one home and a hand-built record cannot dodge it.</summary>
  public bool IsWellFormed =>
      !string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(Citation);

  internal static DomainError MissingText { get; } = new("UncitedFinding",
      "a finding must say what the reviewer observed; the text was empty.");

  internal static DomainError MissingCitation(string text) => new("UncitedFinding",
      $"the finding '{text.Trim()}' cites nothing; name a requirement id or a step position, "
      + "or it cannot be acted on or audited.");

  private static DomainError? RefusalFor(string text, string citation) => (text, citation) switch
  {
    _ when string.IsNullOrWhiteSpace(text) => MissingText,
    _ when string.IsNullOrWhiteSpace(citation) => MissingCitation(text),
    _ => null,
  };
}

/// <summary>The three rubric dimensions a review scores (issue #216 D3): requirement
///     coverage, testability, and blast radius.
///     <para>NAMED DIMENSIONS, NOT A FREE-TEXT SCORE. Named dimensions are what a
///     rubric-scored judge is for: they can be compared across reviews and trended, which
///     free text cannot.</para>
///     <para>THE SCALE IS THE RUBRIC'S, NOT THE RECORD'S. Only non-negativity is enforced
///     here, because that is the one thing the record can know without owning the rubric:
///     pinning a 1..5 range in the schema would make the record the authority on the scale
///     the <c>reviewing-plans</c> skill (#217) defines, and a later rubric that scores 1..10
///     would then need a migration to be recorded at all.</para></summary>
/// <param name="RequirementCoverage">How completely the plan covers its requirements.</param>
/// <param name="Testability">How checkable the plan's steps are.</param>
/// <param name="BlastRadius">How much of the workspace the plan touches.</param>
public sealed record PlanReviewScores(int RequirementCoverage, int Testability, int BlastRadius)
{
  /// <summary>Builds a score set, refusing any negative dimension with a typed
  ///     <c>InvalidRubricScore</c> error naming the dimension and the value.</summary>
  /// <param name="requirementCoverage">How completely the plan covers its requirements.</param>
  /// <param name="testability">How checkable the plan's steps are.</param>
  /// <param name="blastRadius">How much of the workspace the plan touches.</param>
  public static Result<PlanReviewScores> Create(int requirementCoverage, int testability, int blastRadius) =>
      RefusalFor(requirementCoverage, testability, blastRadius) is { } refusal
          ? Result.Failure<PlanReviewScores>(refusal)
          : Result.Success(new PlanReviewScores(requirementCoverage, testability, blastRadius));

  /// <summary>True when every dimension is non-negative - the predicate
  ///     <see cref="PlanReview.Validate"/> reads at write time.</summary>
  public bool IsWellFormed => RequirementCoverage >= 0 && Testability >= 0 && BlastRadius >= 0;

  /// <summary>The first negative dimension as a typed refusal, or null. One guard chain,
  ///     so the rule reads in the order it is stated.</summary>
  internal static DomainError? RefusalFor(int coverage, int testability, int blastRadius) =>
      (coverage, testability, blastRadius) switch
      {
        _ when coverage < 0 => Negative("requirement coverage", coverage),
        _ when testability < 0 => Negative("testability", testability),
        _ when blastRadius < 0 => Negative("blast radius", blastRadius),
        _ => null,
      };

  internal static DomainError Negative(string dimension, int value) => new("InvalidRubricScore",
      $"the {dimension} score is {value}; a rubric score cannot be negative.");
}

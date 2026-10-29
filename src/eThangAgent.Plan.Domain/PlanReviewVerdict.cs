using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>What a reviewer concluded about a plan (issue #216 D2). Exactly three values:
///     a binary gate cannot express the middle case, and forcing conditions into an approve
///     loses the findings that make them actionable.
///     <para>THE STORED TOKEN IS THE TOKEN THE BRIEF PUBLISHES. The review brief (#306)
///     tells a reviewer to write <c>verdict: approve | deny | approve-with-conditions</c>,
///     so the record stores exactly those spellings. That is a deliberate departure from
///     <c>ChainStepRole</c>, which stores its enum member name: there the value is written
///     by the harness, while here the value is written by a MODEL following the brief, and
///     a second spelling would be a second thing to keep in sync.</para>
///     <para>THE VALUES ARE NOT A STATUS. Plan status stays exactly three members
///     (Active/Completed/Abandoned) with its own transition rule: an approval is a fact
///     ABOUT the plan, not a state OF it, and folding it into the status enum would change
///     lifecycle semantics the review record exists to observe rather than alter.</para></summary>
public enum PlanReviewVerdict
{
  /// <summary>The work may advance.</summary>
  Approve,

  /// <summary>The work goes back for a fix.</summary>
  Deny,

  /// <summary>The work may advance once the findings are addressed.</summary>
  ApproveWithConditions,
}

/// <summary>The one place a <see cref="PlanReviewVerdict"/> becomes text and back
///     (issue #216 B3).
///     <para>STRICT, NOT LENIENT: only the three exact tokens decode. A case-insensitive
///     match would be a silent coercion at a boundary the constitution says must be exact,
///     and the brief publishes the exact spelling, so a conforming reviewer writes it. An
///     unknown value is a NAMED failure that quotes what it saw, never a default - a
///     verdict nobody can name is worse than a verdict that failed to parse.</para></summary>
public static class PlanReviewVerdictCodec
{
  private const string ApproveToken = "approve";
  private const string DenyToken = "deny";
  private const string ConditionsToken = "approve-with-conditions";

  /// <summary>Every allowed token, in the order the brief publishes them.</summary>
  public static IReadOnlyList<string> Tokens { get; } =
      [ApproveToken, DenyToken, ConditionsToken];

  /// <summary>The stored text for <paramref name="verdict"/>.</summary>
  /// <param name="verdict">The verdict to encode.</param>
  public static string Encode(PlanReviewVerdict verdict) => verdict switch
  {
    PlanReviewVerdict.Approve => ApproveToken,
    PlanReviewVerdict.Deny => DenyToken,
    PlanReviewVerdict.ApproveWithConditions => ConditionsToken,
    _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict,
        "not a plan review verdict."),
  };

  /// <summary>Reads a verdict. Fails with <c>UnknownVerdict</c> naming the value it saw.
  ///     <para>A null or blank value fails the same way rather than reading as an approve:
  ///     the safe direction for a gate is 'not a verdict', never 'probably fine'.</para></summary>
  /// <param name="value">The text to read.</param>
  public static Result<PlanReviewVerdict> Decode(string? value) => value switch
  {
    ApproveToken => Result.Success(PlanReviewVerdict.Approve),
    DenyToken => Result.Success(PlanReviewVerdict.Deny),
    ConditionsToken => Result.Success(PlanReviewVerdict.ApproveWithConditions),
    _ => Result.Failure<PlanReviewVerdict>(Unknown(value)),
  };

  /// <summary>True when <paramref name="verdict"/> is a defined member. The same guard the
  ///     stores apply to every status column: <c>Enum.IsDefined</c> rejects a numeric string
  ///     that <c>Enum.TryParse</c> would happily turn into an out-of-range value.</summary>
  /// <param name="verdict">The verdict to check.</param>
  public static bool IsDefined(PlanReviewVerdict verdict) => Enum.IsDefined(verdict);

  private static DomainError Unknown(string? value)
  {
    string seen = string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    return new DomainError("UnknownVerdict",
        $"'{seen}' is not a plan review verdict; the allowed values are "
        + string.Join(", ", Tokens) + ".");
  }
}

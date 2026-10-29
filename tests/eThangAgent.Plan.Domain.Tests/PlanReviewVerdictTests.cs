using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>Issue #216 B3: the verdict type admits exactly approve, deny, and
///     approve-with-conditions, and an unknown string fails TYPED rather than defaulting.
///     <para>THE TOKENS ARE THE BRIEF'S. #306's review brief tells a reviewer to write
///     <c>verdict: approve | deny | approve-with-conditions</c>, so the codec's tokens and
///     the brief's vocabulary are one list. A test pins the exact spellings because a drift
///     between them is a reviewer whose conforming verdict fails to parse.</para></summary>
public class PlanReviewVerdictTests
{
  [Theory]
  [InlineData(PlanReviewVerdict.Approve, "approve")]
  [InlineData(PlanReviewVerdict.Deny, "deny")]
  [InlineData(PlanReviewVerdict.ApproveWithConditions, "approve-with-conditions")]
  public void Encode_ProducesTheTokenTheBriefPublishes(PlanReviewVerdict verdict, string expected) =>
    Assert.Equal(expected, PlanReviewVerdictCodec.Encode(verdict));

  [Theory]
  [InlineData("approve", PlanReviewVerdict.Approve)]
  [InlineData("deny", PlanReviewVerdict.Deny)]
  [InlineData("approve-with-conditions", PlanReviewVerdict.ApproveWithConditions)]
  public void Decode_RoundTripsEveryToken(string token, PlanReviewVerdict expected)
  {
    Result<PlanReviewVerdict> decoded = PlanReviewVerdictCodec.Decode(token);
    Assert.True(decoded.IsSuccess, decoded.Error?.Message ?? "expected a decode");
    Assert.Equal(expected, decoded.Value);
  }

  [Fact]
  public void Tokens_AreExactlyTheThreeTheBriefPublishes() =>
    Assert.Equal(["approve", "deny", "approve-with-conditions"], PlanReviewVerdictCodec.Tokens);

  [Theory]
  [InlineData("APPROVE")]
  [InlineData("Approve")]
  [InlineData("approved")]
  [InlineData("no")]
  [InlineData("5")]
  [InlineData("")]
  [InlineData(null)]
  public void Decode_RefusesAnythingElse_WithATypedError(string? value)
  {
    Result<PlanReviewVerdict> decoded = PlanReviewVerdictCodec.Decode(value);
    Assert.False(decoded.IsSuccess, "an unknown verdict must not decode");
    Assert.Equal("UnknownVerdict", decoded.Error.Code);
  }

  [Fact]
  public void Decode_NamesTheValueItSaw()
  {
    Result<PlanReviewVerdict> decoded = PlanReviewVerdictCodec.Decode("maybe");
    Assert.False(decoded.IsSuccess);
    Assert.Contains("'maybe'", decoded.Error.Message, StringComparison.Ordinal);
    Assert.Contains("approve-with-conditions", decoded.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void IsDefined_RejectsAnOutOfRangeCast()
  {
    Assert.True(PlanReviewVerdictCodec.IsDefined(PlanReviewVerdict.Deny));
    Assert.False(PlanReviewVerdictCodec.IsDefined((PlanReviewVerdict)99));
  }
}

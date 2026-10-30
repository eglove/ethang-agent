using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>Issue #320: <c>plan.review</c> - the model-facing writer for a verdict. Strict
///     argument parsing, the denial-without-findings rule, and the record's own refusal codes
///     surfaced verbatim.</summary>
public class PlanReviewActionTests
{
  private static PlanCapabilityProvider New(out List<PlanReviewDraft> written)
  {
    List<PlanReviewDraft> captured = [];
    written = captured;
    return new PlanCapabilityProvider(
        new PlanService(new StubStore()),
        () => "session-reviewer",
        recordReview: (draft, _) =>
        {
          captured.Add(draft);
          return Task.FromResult(Result.Success(new PlanReview(11, draft.PlanId, 3,
              draft.Verdict, "session-reviewer", "vendor/reviewer", draft.Scores,
              draft.Findings, draft.Notes, DateTimeOffset.UnixEpoch)));
        });
  }

  private static async Task<CapabilityInvocationResult> InvokeAsync(PlanCapabilityProvider provider,
      string json) => await provider.InvokeAsync("review", json,
          TestContext.Current.CancellationToken).ConfigureAwait(true);

  private const string Scores =
      "\"scores\":{\"requirementCoverage\":4,\"testability\":2,\"blastRadius\":3}";

  [Fact]
  public void Review_IsOnTheSurface()
  {
    PlanCapabilityProvider provider = New(out _);
    Assert.Contains(provider.Actions, action => action.Name == "review");
  }

  [Fact]
  public async Task Review_OnDenyWithACitedFinding_RecordsItAndReportsWhatWasKept()
  {
    PlanCapabilityProvider provider = New(out List<PlanReviewDraft> written);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"deny\"," + Scores
        + ",\"findings\":[{\"text\":\"step 3 cites nothing\",\"citation\":\"R2\"}]}")
        .ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Contains("[plan] recorded deny review #11 of #7 v3", result.Content, StringComparison.Ordinal);
    // The finding and its citation are echoed, because the citation is what makes it actionable.
    Assert.Contains("step 3 cites nothing [cites: R2]", result.Content, StringComparison.Ordinal);
    PlanReviewDraft draft = Assert.Single(written);
    Assert.Equal(7, draft.PlanId);
    Assert.Equal(PlanReviewVerdict.Deny, draft.Verdict);
    Assert.Equal(4, draft.Scores.RequirementCoverage);
  }

  [Fact]
  public async Task Review_OnDenyWithNoFindings_IsRefused()
  {
    // A fix session told only 'the reviewer denied' would guess, which is the failure the rule
    // exists to prevent (issue #219's surviving rule).
    PlanCapabilityProvider provider = New(out List<PlanReviewDraft> written);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"deny\"," + Scores + ",\"findings\":[]}")
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("at least one cited finding", result.Content, StringComparison.Ordinal);
    Assert.Empty(written);
  }

  [Fact]
  public async Task Review_OnApproveWithNoFindings_IsLegal()
  {
    // A clean approve is a real verdict: there is nothing to act on, so there is nothing to cite.
    PlanCapabilityProvider provider = New(out _);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"approve\"," + Scores + ",\"findings\":[]}")
        .ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }

  [Fact]
  public async Task Review_WithAnUnknownVerdictToken_IsRefusedNamingTheAllowedTokens()
  {
    // STRICT: the brief publishes three exact tokens, and a lenient match would be a silent
    // coercion at a boundary the constitution says must be exact.
    PlanCapabilityProvider provider = New(out _);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"LGTM\"," + Scores + ",\"findings\":[]}")
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("approve-with-conditions", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Review_WithAnUncitedFinding_IsRefusedNamingItsIndex()
  {
    PlanCapabilityProvider provider = New(out List<PlanReviewDraft> written);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"approve-with-conditions\"," + Scores
        + ",\"findings\":[{\"text\":\"first\",\"citation\":\"1\"},"
        + "{\"text\":\"the code is unclear\"}]}")
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("cites nothing", result.Content, StringComparison.Ordinal);
    Assert.Empty(written);
  }

  [Fact]
  public async Task Review_WithANegativeScore_IsRefused()
  {
    PlanCapabilityProvider provider = New(out _);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"approve\","
        + "\"scores\":{\"requirementCoverage\":-1,\"testability\":2,\"blastRadius\":3},"
        + "\"findings\":[]}")
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("cannot be negative", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Review_WithNoRecorderWired_RefusesRatherThanReportingSuccess()
  {
    // Reporting 'recorded' while writing nothing is the silent discard this issue closes,
    // wearing a new coat.
    PlanCapabilityProvider provider = new(new PlanService(new StubStore()), () => "session-reviewer");

    CapabilityInvocationResult result = await InvokeAsync(provider,
        "{\"id\":7,\"verdict\":\"approve\"," + Scores + ",\"findings\":[]}")
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ReviewUnsupported", result.Content, StringComparison.Ordinal);
  }

  /// <summary>A store that always reports one plan, so the action's read succeeds.</summary>
  private sealed class StubStore : IPlanStore
  {
    public Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default)
        => Task.FromResult(Result.Success(draft with { Id = 7, Version = 3 }));

    public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Plan.New("Ship it", "goal", "s", DateTimeOffset.UnixEpoch)
            with
        { Id = id, Version = 3 }));

    public Task<Result<PlanCatalog>> ListAsync(PlanStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new PlanCatalog([], [])));

    public Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(plan));
  }
}

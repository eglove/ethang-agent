using Xunit;

namespace eThangAgent.LiveModel.Tests;

/// <summary>The Tier-2 comparison's own contract, tested WITHOUT a live model: it REPORTS drops
///     and never throws, because a measurement that failed the build would be deleted the first
///     time a model had a bad day (issue #289).
///     <para>No [Trait("Category", "LiveModel")] here on purpose: this half is free and offline,
///     so the mechanism is pinned in every run - the live half is what needs a key.</para></summary>
public class ProcedureBaselineComparisonTests
{
  private static ProcedureObservation Observation(
      int toolCalls = 2,
      int typedErrors = 1,
      int changeReports = 1,
      int messages = 8,
      bool observedBeforeMutating = true,
      bool verifiedAfterChanging = true)
      => new("model/x", 4, 42_119, messages, toolCalls, typedErrors, changeReports,
          observedBeforeMutating, verifiedAfterChanging);

  [Fact]
  public void Drops_EqualObservation_ReportsNothing() =>
      Assert.Empty(ProcedureBaseline.Drops(Observation(), Observation()));

  [Fact]
  public void Drops_Improvement_ReportsNothing()
  {
    // A model that performed BETTER than the baseline is not a regression; only a fall is.
    Assert.Empty(ProcedureBaseline.Drops(Observation(),
        Observation(toolCalls: 5, typedErrors: 3, changeReports: 2, messages: 12)));
  }

  [Fact]
  public void Drops_FewerFacts_IsReportedByName()
  {
    IReadOnlyList<string> drops = ProcedureBaseline.Drops(Observation(),
        Observation(toolCalls: 0, typedErrors: 0, changeReports: 0, messages: 2));

    Assert.Contains(drops, line => line.Contains("toolCalls", StringComparison.Ordinal));
    Assert.Contains(drops, line => line.Contains("typedErrorResults", StringComparison.Ordinal));
    Assert.Contains(drops, line => line.Contains("changeReports", StringComparison.Ordinal));
    Assert.Contains(drops, line => line.Contains("persistedMessages", StringComparison.Ordinal));
  }

  [Fact]
  public void Drops_ProcedurePredicateFlipped_IsReported()
  {
    IReadOnlyList<string> drops = ProcedureBaseline.Drops(Observation(),
        Observation(observedBeforeMutating: false, verifiedAfterChanging: false));

    Assert.Contains(drops, line => line.Contains("observedBeforeMutating", StringComparison.Ordinal));
    Assert.Contains(drops, line => line.Contains("verifiedAfterChanging", StringComparison.Ordinal));
  }

  [Fact]
  public void Read_IsTotal_StoredBaselineOrNull_NeverAnException()
  {
    // A first run must record an observation, never invent a comparison: a missing baseline is
    // an ordinary state, and turning it into an exception would make the suite unrunnable until
    // someone committed a file it had never produced. Reaching the line below IS the assertion -
    // Read either returns the committed baseline or null, and a throw would fail the test.
    ProcedureObservation? stored = ProcedureBaseline.Read();
    Assert.True(stored is null || stored.ModelId.Length > 0,
        "a stored baseline names the model it was recorded on");
  }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using eThangAgent.ConversationDomain;
using Xunit;

namespace eThangAgent.LiveModel.Tests;

/// <summary>TIER 2 of issue #289: PROCEDURE MEASUREMENT - how the model behaved, recorded, never
///     asserted.
///     <para>WHY IT MUST NOT FAIL THE BUILD: the predicates below measure the MODEL's method (did
///     it observe before mutating, reproduce before fixing, verify before claiming done). Model
///     behaviour changes without a commit here, so a red build from these would be a red build
///     nobody can fix - and the first bad day a model has, the assertion gets deleted. A
///     measurement that cannot be deleted is a measurement that gets read.</para>
///     <para>WHAT IT DOES INSTEAD: writes one JSON artifact per run (the resolved model, the fact
///     counts, and the raw predicates) and compares it against a STORED BASELINE, reporting any
///     drop in its own output. The artifact is uploaded by the CI job, so a trend is visible
///     without anyone remembering to look.</para>
///     <para>THE BASELINE IS A COMMITTED FILE (<c>tier2-baseline.json</c> in this project). A
///     first run with no baseline records the observation and says so - it never invents a
///     comparison it cannot make.</para></summary>
[Trait("Category", "LiveModel")]
[Trait("Category", "LiveModelMeasurement")]
public class LiveModelProcedureMeasurementTests
{
  /// <summary>Where the run's artifact lands. The CI job uploads this path; a local run keeps it
  ///     beside the test binary.</summary>
  public static string ArtifactPath => Path.Combine(AppContext.BaseDirectory, "tier2-live-run.json");

  [Fact]
  public async Task ProcedurePredicates_AreRecordedNotAsserted()
  {
    _ = LiveModelEnvironment.RequireApiKey();
    LiveRunArtifacts run = await LiveModelSession.RunSequenceAsync().ConfigureAwait(true);

    ProcedureObservation observation = ProcedureObservation.From(run);
    await File.WriteAllTextAsync(ArtifactPath,
        JsonSerializer.Serialize(observation, JsonOptions), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    // RECORDED, PRINTED, NEVER ASSERTED: the report lines are the deliverable. The comparison
    // against the stored baseline REPORTS a drop and still passes - a bad model day is not a
    // broken harness, and a red build from this would be deleted the first time it fired.
    ProcedureObservation? baseline = ProcedureBaseline.Read();
    string verdict = baseline is null
        ? $"[tier2] no stored baseline ({ProcedureBaseline.FileName}); this run records one. "
            + "Commit the artifact as the baseline to enable comparisons."
        : DescribeComparison(ProcedureBaseline.Drops(baseline, observation));
    Console.WriteLine(observation.Describe());
    Console.WriteLine(verdict);
    Assert.True(true, observation.Describe() + " | " + verdict);
  }

  private static string DescribeComparison(IReadOnlyList<string> drops) => drops.Count == 0
      ? "[tier2] no drop against the stored baseline"
      : "[tier2] DROP against the stored baseline (reported, not failed): " + string.Join("; ", drops);

  private static JsonSerializerOptions JsonOptions { get; } = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };
}

/// <summary>One run's procedure predicates, in the shape the artifact stores.
///     <para>Each predicate is DERIVED ONLY from the persisted transcript, so it measures what
///     actually happened across the wire rather than what a live object happened to hold. They
///     are deliberately crude - a regex over tool arguments and results - because a sophisticated
///     classifier would itself need a live model to validate.</para></summary>
internal sealed record ProcedureObservation(
    string ModelId,
    int ProviderCalls,
    long InputTokens,
    int PersistedMessages,
    int ToolCalls,
    int TypedErrorResults,
    int ChangeReports,
    bool ObservedBeforeMutating,
    bool VerifiedAfterChanging)
{
  /// <summary>Reads the tool calls and results out of one persisted run.</summary>
  public static ProcedureObservation From(LiveRunArtifacts run)
  {
    ArgumentNullException.ThrowIfNull(run);
    List<ToolCall> calls = [.. run.PersistedTranscript.SelectMany(message => message.ToolCalls ?? [])];
    IReadOnlyList<string> results = run.ToolResultTexts();

    // OBSERVE BEFORE MUTATE: the first call that could write comes AFTER at least one call that
    // only reads. Crude on purpose - `read`/`find_files`/`db_query` are read-shaped; `exec`,
    // `write`, `edit` are write-shaped.
    int firstRead = IndexOfFirst(calls, name => name is "read" or "find_files" or "db_query" or "sqlite_query");
    int firstWrite = IndexOfFirst(calls, name => name is "exec" or "write" or "edit");
    bool observedBeforeMutating = firstWrite < 0 || (firstRead >= 0 && firstRead < firstWrite);

    // VERIFY AFTER CHANGING: a run that changed a file also ran something afterwards whose result
    // it could check (a read-back, a query, or another exec).
    bool verifiedAfterChanging = firstWrite < 0 || calls.Count > firstWrite + 1;

    return new ProcedureObservation(
        run.Model.ModelId,
        run.Usage.Totals.Calls,
        run.Usage.Totals.InputTokens,
        run.PersistedTranscript.Count,
        calls.Count,
        results.Count(text => text.Contains("Error [", StringComparison.Ordinal)),
        results.Count(text => text.Contains("[exec: modified ", StringComparison.Ordinal)),
        observedBeforeMutating,
        verifiedAfterChanging);
  }

  private static int IndexOfFirst(List<ToolCall> calls, Func<string, bool> matches)
  {
    for (int i = 0; i < calls.Count; i++)
    {
      if (matches(calls[i].Name))
      {
        return i;
      }
    }

    return -1;
  }

  /// <summary>The one-line report. It reads as a measurement, never as a verdict.</summary>
  public string Describe() => string.Create(CultureInfo.InvariantCulture,
      $"[tier2] model={ModelId} calls={ProviderCalls} input={InputTokens} messages={PersistedMessages} "
      + $"toolCalls={ToolCalls} typedErrors={TypedErrorResults} changeReports={ChangeReports} "
      + $"observedBeforeMutating={ObservedBeforeMutating} verifiedAfterChanging={VerifiedAfterChanging}");
}

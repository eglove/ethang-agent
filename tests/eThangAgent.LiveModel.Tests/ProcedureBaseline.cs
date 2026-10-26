using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace eThangAgent.LiveModel.Tests;

/// <summary>The stored Tier-2 baseline and the comparison against it (issue #289).
///     <para>A BASELINE IS A RECORDED OBSERVATION OF ONE RUN, committed beside the tests. The
///     comparison reports DROPS - a run whose fact counts fell below the baseline, or whose
///     procedure predicate flipped from true to false - and never throws. A measurement that
///     failed the build would be deleted the first time a model had a bad day; a measurement that
///     is merely REPORTED survives to be read.</para>
///     <para>THE ARTIFACT IS THE TREND: the run writes what it observed, so an uploaded file per
///     CI run shows the direction without anyone remembering to look.</para></summary>
internal static class ProcedureBaseline
{
  /// <summary>The committed baseline's file name, resolved beside the test assembly's project.
  ///     A missing file is an ordinary first-run state, never an error.</summary>
  public const string FileName = "tier2-baseline.json";

  private static JsonSerializerOptions JsonOptions { get; } = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  /// <summary>The baseline beside the test binary (it is copied to the output by the project
  ///     file), or null when none has been stored yet.</summary>
  public static ProcedureObservation? Read()
  {
    string path = Path.Combine(AppContext.BaseDirectory, FileName);
    if (!File.Exists(path))
    {
      return null;
    }

    string json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<ProcedureObservation>(json, JsonOptions);
  }

  /// <summary>The reported differences between a baseline and a fresh observation, one line each.
  ///     An EMPTY list means nothing dropped (or there was no baseline to compare against, which
  ///     the measurement test states separately).</summary>
  public static IReadOnlyList<string> Drops(ProcedureObservation baseline, ProcedureObservation current)
  {
    ArgumentNullException.ThrowIfNull(baseline);
    ArgumentNullException.ThrowIfNull(current);
    List<string> drops = [];
    Compare(drops, "toolCalls", baseline.ToolCalls, current.ToolCalls);
    Compare(drops, "typedErrorResults", baseline.TypedErrorResults, current.TypedErrorResults);
    Compare(drops, "changeReports", baseline.ChangeReports, current.ChangeReports);
    Compare(drops, "persistedMessages", baseline.PersistedMessages, current.PersistedMessages);
    if (baseline.ObservedBeforeMutating && !current.ObservedBeforeMutating)
    {
      drops.Add("observedBeforeMutating flipped true -> false");
    }

    if (baseline.VerifiedAfterChanging && !current.VerifiedAfterChanging)
    {
      drops.Add("verifiedAfterChanging flipped true -> false");
    }

    return drops;
  }

  private static void Compare(List<string> drops, string name, int baseline, int current)
  {
    if (current < baseline)
    {
      drops.Add(string.Create(CultureInfo.InvariantCulture,
          $"{name} dropped {baseline} -> {current}"));
    }
  }
}

using System.Globalization;
using System.Text;

namespace eThangAgent.AgentDomain;

/// <summary>One measured metric, its value, and whether it cleared the threshold that was fixed
///     before the measurement existed (issue #309 D4/D5).
///     <para><paramref name="Value"/> IS NULLABLE AND NULL MEANS NOT MEASURED — never zero. A
///     deny rate of 0% (a reviewer that approved everything) and 'there were no review steps to
///     judge' are different facts, and the issue's own acceptance criterion says a zero and an
///     unmeasured value must never render alike. The report states which it is.</para>
///     <para><paramref name="Breached"/> is null for an unmeasured metric for the same reason: a
///     threshold nothing was compared against has not been met, it has not been TESTED, and
///     reporting that as a pass would be the green gate implying a judgment nobody made.</para>
///     </summary>
/// <param name="Name">The metric's stable name — the same string the threshold artifact keys on,
///     so a reader can join the two without a table.</param>
/// <param name="Value">The measured value, or null when nothing in scope could measure it.</param>
/// <param name="Threshold">The threshold it is compared against, rendered.</param>
/// <param name="Breached">True when it missed the threshold, false when it cleared it, null when
///     there was nothing to compare.</param>
/// <param name="Detail">The counts the value was computed from, so a reader can check the
///     arithmetic rather than trusting the ratio.</param>
public sealed record ChainTelemetryMetric(string Name, decimal? Value, string Threshold,
    bool? Breached, string Detail)
{
  /// <summary>The one line a report renders. An unmeasured metric says so IN WORDS, because a
  ///     blank or a zero where a measurement belongs is the failure mode this record exists to
  ///     prevent.</summary>
  public string Render()
  {
    string state = Breached switch
    {
      true => "BREACH",
      false => "ok",
      null => "NOT MEASURED",
    };
    string shown = Value is { } value
        ? value.ToString("0.###", CultureInfo.InvariantCulture)
        : "-";
    return string.Create(CultureInfo.InvariantCulture,
        $"  {Name}: {shown} (threshold {Threshold}) [{state}] - {Detail}");
  }
}

/// <summary>One park reason and how many chains it accounts for (issue #309).
///     <para>THE REASON IS CARRIED VERBATIM, never bucketed into a category: the issue's
///     threshold is 'no single park reason above half of all parks', and bucketing would invent
///     the categories the user is meant to read. An unattributed park is its own
///     <see cref="ChainTelemetryReport.UnattributedParkReason"/> entry, counted separately so it
///     can never be folded into a real reason.</para></summary>
/// <param name="Reason">The recorded park reason, or the unattributed marker.</param>
/// <param name="Count">How many chains parked for it.</param>
public sealed record ChainTelemetryParkReason(string Reason, int Count);

/// <summary>The telemetry report (issue #309): what the persisted rows say about how chains ran,
///     measured against thresholds that were fixed before the first report existed.
///     <para>EVERY NUMBER COMES FROM A STORED ROW — the chain tables, the usage ledger's own
///     rows, and the plan review record. Nothing is self-reported by a session, and nothing is
///     read from a clock at report time, which is what makes the report REPRODUCIBLE: the same
///     rows produce byte-identical output.</para>
///     <para>COST IS NEVER RECORDED HERE (D1). A second cost number would eventually disagree
///     with the ledger, and the ledger is the one that is reconciled; this sums the ledger's own
///     rows over the sessions the chain started.</para>
///     <para>STEP OUTCOMES ARE READ FROM THE CHAIN RECORD (D2), never from a separate telemetry
///     table: the chain already stores status, attempts, review attempts and the clocks, and a
///     duplicate would be a second source that can disagree with the record the harness acts on.
///     </para></summary>
/// <param name="ChainCount">How many chains were in scope.</param>
/// <param name="Metrics">Every metric, in the order the issue lists them.</param>
/// <param name="ParkReasons">Park counts per reason, most frequent first.</param>
/// <param name="Baseline">The subagent-era baseline, or null when nothing comparable exists.
///     </param>
/// <param name="Notes">Facts a reader needs to read the numbers correctly — an empty scope, an
///     incomparable baseline, an excluded row. Empty when there is nothing to say.</param>
public sealed record ChainTelemetryReport(int ChainCount, IReadOnlyList<ChainTelemetryMetric> Metrics,
    IReadOnlyList<ChainTelemetryParkReason> ParkReasons, ChainTelemetryBaseline? Baseline,
    IReadOnlyList<string> Notes)
{
  /// <summary>The reason entry an unattributed park is counted under.
  ///     <para>ITS OWN BUCKET, never merged: an unattributed park is a REPORTING defect, and
  ///     folding it into a reason bucket would let a bug look like a finding.</para></summary>
  public const string UnattributedParkReason = "(no reason recorded)";

  /// <summary>True when at least one metric was measured. A report over an empty workspace
  ///     measures nothing, and it says so rather than rendering a wall of zeros.</summary>
  public bool HasMeasurements => Metrics.Any(metric => metric.Value is not null);

  /// <summary>The metrics that were measured and missed their threshold, in report order.</summary>
  public IReadOnlyList<ChainTelemetryMetric> Breaches =>
      [.. Metrics.Where(metric => metric.Breached == true)];

  /// <summary>The rendered report. ONE RENDERER, so two callers cannot present the same rows
  ///     differently — the <c>UsageCost</c> precedent applied to a surface.</summary>
  public string Render()
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[telemetry] {ChainCount} chain(s) in scope, {Breaches.Count} threshold(s) breached\n");
    foreach (string note in Notes)
    {
      _ = sb.Append("  note: ").Append(note).Append('\n');
    }

    foreach (ChainTelemetryMetric metric in Metrics)
    {
      _ = sb.Append(metric.Render()).Append('\n');
    }

    AppendParkReasons(sb);
    AppendBaseline(sb);
    return sb.ToString().TrimEnd('\n');
  }

  /// <summary>The park-reason distribution, rendered only when something parked: a header with
  ///     no rows under it reads as a rendering bug.</summary>
  private void AppendParkReasons(StringBuilder sb)
  {
    if (ParkReasons.Count == 0)
    {
      return;
    }

    _ = sb.Append("  parks by reason:\n");
    foreach (ChainTelemetryParkReason reason in ParkReasons)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"    {reason.Count} x {reason.Reason}\n");
    }
  }

  /// <summary>The baseline comparison, rendered only when there is one — and saying INCOMPARABLE
  ///     when the rows exist but cannot be compared, because asserting a comparison that could
  ///     not be computed is the failure the threshold exists to prevent.</summary>
  private void AppendBaseline(StringBuilder sb)
  {
    if (Baseline is not { } baseline)
    {
      return;
    }

    _ = sb.Append("  baseline (subagent era, from persisted rows):\n");
    foreach (string line in baseline.Render())
    {
      _ = sb.Append("    ").Append(line).Append('\n');
    }
  }
}

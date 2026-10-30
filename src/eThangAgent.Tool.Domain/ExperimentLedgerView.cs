namespace eThangAgent.ToolDomain;

/// <summary>One ledger line as a READER sees it (issue #318): the text exactly as stored, plus
///     the classification the ledger's own parser derived from it.
///     <para>THE RAW TEXT IS WHAT RENDERS (D5). <see cref="Raw"/> is the stored line verbatim -
///     never a re-rendering of the parsed pieces - so a session that wrote something slightly
///     unusual still shows the user what it actually wrote. The classification is carried
///     ALONGSIDE the text, never instead of it.</para>
///     <para>WHY THERE IS A CLASSIFICATION AT ALL: D2 needs each ledger's open-prediction count
///     and D3 needs a refuted verdict to be scannable, and both are facts about the record kind.
///     D5 forbids a SECOND definition of the format, not the use of the first one - so this
///     reader reuses <see cref="ExperimentLedger"/>, the single parser the tool itself writes
///     through. A view-side parser would be the second definition D5 exists to prevent.</para>
///     </summary>
/// <param name="Kind">The record kind the line parses as.</param>
/// <param name="Raw">The stored line, verbatim.</param>
/// <param name="Verdict">A verdict line's verdict; null for every other kind.</param>
public sealed record ExperimentLedgerLine(ExperimentRecordKind Kind, string Raw, ExperimentVerdict? Verdict)
{
  /// <summary>True for a REFUTED verdict - the highest-value record in a ledger (D3), because
  ///     it is what stops the next session spending budget on something already disproved.
  ///     <para>A property rather than a test at each call site: a view that asked
  ///     'is Kind Verdict and is Verdict Refuted' in three places would eventually ask it
  ///     differently in one of them.</para></summary>
  public bool IsRefuted => Kind == ExperimentRecordKind.Verdict && Verdict == ExperimentVerdict.Refuted;
}

/// <summary>A whole ledger read for display (issue #318): its lines in order, how many records
///     it holds, and how many predictions are still open - the number D2 puts in the ledger
///     list, and the same number the finish gate reads.
///     <para>THE COUNTS AND THE LINES COME FROM ONE PARSE, so the number the list shows and the
///     records the detail view renders cannot disagree.</para></summary>
/// <param name="RecordCount">How many records the ledger holds.</param>
/// <param name="OpenPredictions">Predictions carrying neither a verdict nor a skip.</param>
/// <param name="Lines">The records, in stored order.</param>
public sealed record ExperimentLedgerSnapshot(int RecordCount, int OpenPredictions,
    IReadOnlyList<ExperimentLedgerLine> Lines)
{
  /// <summary>An empty ledger: no records, nothing open. What a key that does not exist yet
  ///     reads as - an absent ledger and an empty one are the same fact to a reader.</summary>
  public static ExperimentLedgerSnapshot Empty { get; } = new(0, 0, []);
}

/// <summary>The one public read entry point over a ledger's stored value (issue #318).
///     <para>IT DELEGATES TO <see cref="ExperimentLedger"/>, the parser the experiment tool
///     itself uses, so the format has exactly one definition (D5). What this adds is a public
///     surface: the parser is internal because the tool owns the write side, while the
///     workbench's ledger view is a legitimate outside reader.</para>
///     <para>NEVER THROWS. A line the parser did not write becomes
///     <see cref="ExperimentRecordKind.Unrecognized"/> and still renders, which is what keeps
///     a prose ledger readable instead of blank.</para></summary>
public static class ExperimentLedgerReader
{
  /// <summary>Reads a stored ledger value into displayable lines with their counts.</summary>
  /// <param name="value">The stored value; null or blank is an empty ledger.</param>
  public static ExperimentLedgerSnapshot Read(string? value)
  {
    IReadOnlyList<ExperimentRecord> records = ExperimentLedger.Parse(value);
    return new ExperimentLedgerSnapshot(
        records.Count,
        ExperimentLedger.OpenPredictions(records),
        [.. records.Select(record => new ExperimentLedgerLine(record.Kind, record.Raw, record.Verdict))]);
  }
}

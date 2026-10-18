namespace eThangAgent.ToolDomain;

/// <summary>The record kinds the experiment tool writes to a ledger, plus
///     <see cref="Unrecognized"/> for a stored line the tool did not write.</summary>
internal enum ExperimentRecordKind { Unrecognized, Hypothesis, Prediction, Verdict, Observation, Conclusion, Skip }

/// <summary>One parsed ledger line. <see cref="Id"/> is the record's own sequence
///     number (0 for an unrecognized line), <see cref="Reference"/> the record it
///     points at (a prediction's hypothesis, a verdict's or closing skip's prediction).
///     <see cref="Text"/> is the line's remaining content verbatim and <see cref="Raw"/> the
///     whole stored line, so a listing can echo the ledger verbatim.</summary>
internal sealed record ExperimentRecord(
    ExperimentRecordKind Kind, int Id, int? Reference, string Text, ExperimentVerdict? Verdict, string Raw);

/// <summary>Reads and writes the append-only experiment ledger stored under
///     <c>sci.&lt;slug&gt;/ledger</c>: the verbatim line shapes the finish gate parses,
///     and a tolerant parser that turns stored lines back into typed records. The
///     parser never throws: a line it did not write (the sdd.* ledgers are prose)
///     becomes an <see cref="ExperimentRecordKind.Unrecognized"/> record that counts
///     toward the record total but never toward open predictions or an id lookup.</summary>
internal static class ExperimentLedger
{
  /// <summary>The marker separating a prediction's claim from its falsifying check.</summary>
  internal const string FalsifiedByMarker = " \u2014 falsified by: ";

  /// <summary>The marker separating a verdict word from its evidence.</summary>
  internal const string EvidenceMarker = " \u2014 ";

  private static readonly Dictionary<string, ExperimentRecordKind> KindWords = new(StringComparer.Ordinal)
  {
    ["hypothesis"] = ExperimentRecordKind.Hypothesis,
    ["prediction"] = ExperimentRecordKind.Prediction,
    ["verdict"] = ExperimentRecordKind.Verdict,
    ["observation"] = ExperimentRecordKind.Observation,
    ["conclusion"] = ExperimentRecordKind.Conclusion,
    ["skip"] = ExperimentRecordKind.Skip,
  };

  /// <summary>The ledger key for a plan slug.</summary>
  internal static string KeyFor(string slug) => $"sci.{slug}/ledger";

  /// <summary>The record kind's ledger word (the line's first token).</summary>
  internal static string KindWord(ExperimentRecordKind kind) => kind switch
  {
    ExperimentRecordKind.Hypothesis => "hypothesis",
    ExperimentRecordKind.Prediction => "prediction",
    ExperimentRecordKind.Verdict => "verdict",
    ExperimentRecordKind.Observation => "observation",
    ExperimentRecordKind.Conclusion => "conclusion",
    ExperimentRecordKind.Skip => "skip",
    ExperimentRecordKind.Unrecognized => "unrecognized",
    // Unnamed enum values cannot occur.
    _ => throw new InvalidOperationException("Unknown experiment record kind."),
  };

  /// <summary>The ledger's lines, in order. A null or blank value is an empty ledger;
  ///     blank trailing lines are dropped so the last record is the last line.</summary>
  internal static IReadOnlyList<string> Lines(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return [];
    }

    List<string> lines = [.. value
        .Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Reverse()
        .SkipWhile(line => line.Length == 0)
        .Reverse()];
    return lines;
  }

  /// <summary>Parses a stored ledger value into typed records, one per line.</summary>
  internal static IReadOnlyList<ExperimentRecord> Parse(string? value) =>
      [.. Lines(value).Select(ParseLine)];

  /// <summary>The next free id for <paramref name="kind"/>: max existing + 1, from 1.</summary>
  internal static int NextId(IReadOnlyList<ExperimentRecord> records, ExperimentRecordKind kind) =>
      records.Where(r => r.Kind == kind).Select(r => r.Id).DefaultIfEmpty(0).Max() + 1;

  /// <summary>True when a record of <paramref name="kind"/> carries <paramref name="id"/>.</summary>
  internal static bool Contains(IReadOnlyList<ExperimentRecord> records, ExperimentRecordKind kind, int id) =>
      records.Any(r => r.Kind == kind && r.Id == id);

  /// <summary>The number of predictions carrying neither a verdict nor a skip —
  ///     exactly the number the finish gate reads.</summary>
  internal static int OpenPredictions(IReadOnlyList<ExperimentRecord> records) =>
      records.Count(r => r.Kind == ExperimentRecordKind.Prediction && !IsClosed(records, r.Id));

  private static bool IsClosed(IReadOnlyList<ExperimentRecord> records, int predictionId) =>
      records.Any(r => r.Kind is ExperimentRecordKind.Verdict or ExperimentRecordKind.Skip &&
          r.Reference == predictionId);

  /// <summary>Parses one line; anything that does not match a documented shape becomes
  ///     an unrecognized record rather than an exception.</summary>
  private static ExperimentRecord ParseLine(string line)
  {
    int cursor = 0;
    string? kindWord = ReadWord(line, ref cursor);
    if (kindWord is null || !KindWords.TryGetValue(kindWord, out ExperimentRecordKind kind))
    {
      return Unrecognized(line);
    }

    // The documented shapes put exactly one space between the kind word and the id.
    if (!TrySkipSpace(line, ref cursor) || !TryReadNumber(line, ref cursor, out int id))
    {
      return Unrecognized(line);
    }

    int? reference = null;
    if (TryReadReference(line, ref cursor, out int referenceId))
    {
      reference = referenceId;
    }

    string? rest = ReadRest(line, ref cursor);
    return rest is null
      ? Unrecognized(line)
      : BuildRecord(kind, id, reference, rest, line);
  }

  private static ExperimentRecord BuildRecord(ExperimentRecordKind kind, int id, int? reference,
      string rest, string line) =>
      kind == ExperimentRecordKind.Verdict
        ? ParseVerdictRecord(id, reference, rest, line)
        : new ExperimentRecord(kind, id, reference, rest, null, line);

  private static ExperimentRecord ParseVerdictRecord(int id, int? reference, string rest, string line)
  {
    int split = rest.IndexOf(EvidenceMarker, StringComparison.Ordinal);
    string word = split < 0 ? rest : rest[..split];
    ExperimentVerdict? verdict = word switch
    {
      "confirmed" => ExperimentVerdict.Confirmed,
      "refuted" => ExperimentVerdict.Refuted,
      "inconclusive" => ExperimentVerdict.Inconclusive,
      _ => null,
    };
    return verdict is { } parsed
      ? new ExperimentRecord(ExperimentRecordKind.Verdict, id, reference, rest, parsed, line)
      : Unrecognized(line);
  }

  private static ExperimentRecord Unrecognized(string line) =>
      new(ExperimentRecordKind.Unrecognized, 0, null, line, null, line);

  /// <summary>Reads the leading letter word (a kind word) and advances the cursor past it.</summary>
  private static string? ReadWord(string line, ref int cursor)
  {
    int start = cursor;
    while (cursor < line.Length && char.IsLetter(line[cursor]))
    {
      cursor++;
    }

    return cursor == start ? null : line[start..cursor];
  }

  /// <summary>Skips one separating space; false when the line carries none.</summary>
  private static bool TrySkipSpace(string line, ref int cursor)
  {
    if (cursor >= line.Length || line[cursor] != ' ')
    {
      return false;
    }

    cursor++;
    return true;
  }

  /// <summary>Reads the decimal id that follows the kind word.</summary>
  private static bool TryReadNumber(string line, ref int cursor, out int value)
  {
    int start = cursor;
    while (cursor < line.Length && char.IsAsciiDigit(line[cursor]))
    {
      cursor++;
    }

    return int.TryParse(line[start..cursor], out value) && value > 0;
  }

  /// <summary>Reads the optional <c> (kind id)</c> reference group. The cursor is left
  ///     where it was when no group is present, so the caller can read the tail.</summary>
  private static bool TryReadReference(string line, ref int cursor, out int referenceId)
  {
    referenceId = 0;
    int start = cursor;
    if (start < line.Length && line[start] == ' ')
    {
      start++;
    }

    if (start >= line.Length || line[start] != '(')
    {
      return false;
    }

    // The group is '(kind id)': one space separates its kind word from its id.
    int scan = start + 1;
    if (ReadWord(line, ref scan) is null || !TrySkipSpace(line, ref scan) ||
        !TryReadNumber(line, ref scan, out referenceId) ||
        scan >= line.Length || line[scan] != ')')
    {
      return false;
    }

    cursor = scan + 1;
    return true;
  }

  /// <summary>Reads the <c>: content</c> tail; null when the colon is missing.</summary>
  private static string? ReadRest(string line, ref int cursor)
  {
    if (cursor < line.Length && line[cursor] == ' ')
    {
      cursor++;
    }

    if (cursor >= line.Length || line[cursor] != ':')
    {
      return null;
    }

    cursor++;
    if (cursor < line.Length && line[cursor] == ' ')
    {
      cursor++;
    }

    return line[cursor..];
  }
}

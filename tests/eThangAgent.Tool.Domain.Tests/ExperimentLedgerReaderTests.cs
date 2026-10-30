namespace eThangAgent.ToolDomain.Tests;

/// <summary>Issue #318 D5: the ledger view renders the stored lines FAITHFULLY rather than
///     parsing them into a schema of its own. These tests pin the reader the view is built on,
///     and in particular that it adds no second definition of the format: every shape comes from
///     <see cref="ExperimentLedger"/>, the parser the experiment tool itself writes through.
///     </summary>
public class ExperimentLedgerReaderTests
{
  /// <summary>The stored line is what renders. A reader that reassembled it from the parsed
  ///     pieces would reformat anything the parser understood, and would be a second definition
  ///     of the format.</summary>
  [Fact]
  public void Read_ReturnsEachStoredLineVerbatim()
  {
    string stored =
        "hypothesis 1: the watcher sees changes\n" +
        "prediction 1 (hypothesis 1): a no-op rewrite fires it \u2014 falsified by: touch f.txt\n" +
        "verdict 1 (prediction 1): refuted \u2014 two Changed events, bytes identical";

    ExperimentLedgerSnapshot snapshot = ExperimentLedgerReader.Read(stored);

    Assert.Equal(stored.Split('\n'), snapshot.Lines.Select(line => line.Raw));
  }

  /// <summary>D2's number: a prediction is open until a verdict OR a skip closes it - exactly
  ///     the count the finish gate reads, so the list and the gate cannot disagree.</summary>
  [Fact]
  public void Read_CountsOnlyPredictionsWithoutAVerdictOrSkip()
  {
    string stored =
        "hypothesis 1: h\n" +
        "prediction 1 (hypothesis 1): open \u2014 falsified by: a\n" +
        "prediction 2 (hypothesis 1): judged \u2014 falsified by: b\n" +
        "verdict 2 (prediction 2): confirmed \u2014 c\n" +
        "prediction 3 (hypothesis 1): skipped \u2014 falsified by: d\n" +
        "skip 1 (prediction 3): e";

    ExperimentLedgerSnapshot snapshot = ExperimentLedgerReader.Read(stored);

    Assert.Equal(1, snapshot.OpenPredictions);
    Assert.Equal(6, snapshot.RecordCount);
  }

  /// <summary>D3's fact: only a REFUTED verdict is marked, so a confirmed one never renders as
  ///     a refutation. The two are different facts and the whole point of the distinction is
  ///     that a reader can trust it.</summary>
  [Fact]
  public void Read_MarksOnlyARefutedVerdict()
  {
    ExperimentLedgerSnapshot refuted = ExperimentLedgerReader.Read(
        "hypothesis 1: h\nprediction 1 (hypothesis 1): p \u2014 falsified by: a\n" +
        "verdict 1 (prediction 1): refuted \u2014 b");
    ExperimentLedgerSnapshot confirmed = ExperimentLedgerReader.Read(
        "hypothesis 1: h\nprediction 1 (hypothesis 1): p \u2014 falsified by: a\n" +
        "verdict 1 (prediction 1): confirmed \u2014 b");

    Assert.Equal(1, refuted.Lines.Count(line => line.IsRefuted));
    Assert.Equal(0, confirmed.Lines.Count(line => line.IsRefuted));
  }

  /// <summary>D5's other half: a line the parser did not write still RENDERS, as
  ///     <see cref="ExperimentRecordKind.Unrecognized"/>. A prose ledger is a legitimate thing
  ///     to find, and rendering it as an invented record would be worse than rendering it as
  ///     what it is.</summary>
  [Fact]
  public void Read_RendersUnrecognizedLinesWithoutInventingAKind()
  {
    ExperimentLedgerSnapshot snapshot = ExperimentLedgerReader.Read(
        "this is prose\nso is this\nhypothesis 1: a real record");

    Assert.Equal(3, snapshot.Lines.Count);
    Assert.Equal(ExperimentRecordKind.Unrecognized, snapshot.Lines[0].Kind);
    Assert.Equal(ExperimentRecordKind.Unrecognized, snapshot.Lines[1].Kind);
    Assert.Equal(ExperimentRecordKind.Hypothesis, snapshot.Lines[2].Kind);
    Assert.Equal("this is prose", snapshot.Lines[0].Raw);
    // An unrecognized line opens nothing: it is not a prediction, so it cannot leave one open.
    Assert.Equal(0, snapshot.OpenPredictions);
  }

  /// <summary>A null or blank value is an EMPTY ledger, not a failure: the experiment tool
  ///     creates the key on the first append, so an untouched plan has no value at all.</summary>
  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   \n  ")]
  public void Read_ANullOrBlankValue_IsAnEmptyLedger(string? value)
  {
    ExperimentLedgerSnapshot snapshot = ExperimentLedgerReader.Read(value);

    Assert.Equal(0, snapshot.RecordCount);
    Assert.Equal(0, snapshot.OpenPredictions);
    Assert.Empty(snapshot.Lines);
  }

  /// <summary>The key's shape, stated once: <c>sci.&lt;slug&gt;/ledger</c>. A slug may contain
  ///     dots, so the namespace test is a prefix rather than an equality - and a key outside the
  ///     shape is not a ledger, so a view never offers one the tool would not read.</summary>
  [Theory]
  [InlineData("sci.my-plan/ledger", true)]
  [InlineData("sci.318.ledger.view/ledger", true)]
  [InlineData("sci.my-plan/notes", false)]
  [InlineData("sdd.my-plan/ledger", false)]
  [InlineData("ledger", false)]
  [InlineData("sci./ledger", false)]
  public void IsLedger_MatchesOnlyTheLedgerKeyShape(string key, bool expected) =>
      Assert.Equal(expected, ExperimentLedgerKey.IsLedger(key));

  /// <summary>The slug is DERIVED from the key, never stored beside it.</summary>
  [Fact]
  public void SlugOf_StripsTheNamespacePrefix()
  {
    Assert.Equal("my-plan", ExperimentLedgerKey.SlugOf("sci.my-plan/ledger"));
    Assert.Equal("318.ledger.view", ExperimentLedgerKey.SlugOf("sci.318.ledger.view/ledger"));
  }
}

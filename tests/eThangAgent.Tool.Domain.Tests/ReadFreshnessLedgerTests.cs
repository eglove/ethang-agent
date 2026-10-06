namespace eThangAgent.ToolDomain.Tests;

/// <summary>T2: the turn-local read ledger. Coverage is range-granular at one file
///     version: Check reports which spans of a requested range the model already
///     holds (delivered earlier at the SAME version); Record unions delivered ranges
///     (adjacent ranges merge); a version change wipes the path's entry wholesale;
///     Reset clears everything (turn start). All operations are thread-safe.</summary>
public class ReadFreshnessLedgerTests
{
  private static readonly DateTime Mtime = new(638000000000000000, DateTimeKind.Utc);
  private static readonly FileVersion V1 = new(100, Mtime);
  private static readonly FileVersion V2 = new(200, Mtime);

  // ---- first read: never covered ----

  [Fact]
  public void Check_FirstReadOfPath_NothingCovered()
  {
    ReadFreshnessLedger ledger = new();
    FileCoverage c = ledger.Check("f.txt", V1, 1, 100);
    Assert.Empty(c.Elided);
  }

  [Fact]
  public void Check_NullVersion_NothingCovered_Legacy()
  {
    ReadFreshnessLedger ledger = new();
    FileCoverage c = ledger.Check("f.txt", null, 1, 100);
    Assert.Empty(c.Elided);
  }

  // ---- record then check: covered ----

  [Fact]
  public void Check_AfterRecordSameVersion_FullyCovered()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 100);
    FileCoverage c = ledger.Check("f.txt", V1, 1, 100);
    LineSpan[] expected = [new LineSpan(1, 100)];
    Assert.Equal(expected, c.Elided);
  }

  [Fact]
  public void Check_SubrangeOfRecorded_IsCovered()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 100);
    FileCoverage c = ledger.Check("f.txt", V1, 10, 50);
    LineSpan[] expected = [new LineSpan(10, 50)];
    Assert.Equal(expected, c.Elided);
  }

  // ---- partial coverage: only the held span elides ----

  [Fact]
  public void Check_PartialOverlap_OnlyOverlappingSpanElides()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 10, 50);
    FileCoverage c = ledger.Check("f.txt", V1, 1, 100);
    // held: 10-50; requested 1-100 -> elide 10-50 only
    LineSpan[] expected = [new LineSpan(10, 50)];
    Assert.Equal(expected, c.Elided);
  }

  [Fact]
  public void Check_WidenedRange_HeadAndTailElided()
  {
    // The chunking pattern: read 10-50 first, then widen to 1-100.
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 10, 50);
    FileCoverage c = ledger.Check("f.txt", V1, 1, 100);
    Assert.Equal([new LineSpan(10, 50)], c.Elided);
  }

  // ---- adjacency and merging ----

  [Fact]
  public void Check_AdjacentRecordedRanges_MergeIntoOneSpan()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 50);
    ledger.Record("f.txt", V1, 51, 100);
    FileCoverage c = ledger.Check("f.txt", V1, 1, 100);
    Assert.Equal([new LineSpan(1, 100)], c.Elided);
  }

  [Fact]
  public void Check_DisjointRecordedRanges_StaySeparate()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 10);
    ledger.Record("f.txt", V1, 20, 30);
    FileCoverage c = ledger.Check("f.txt", V1, 1, 30);
    LineSpan[] expected = [new LineSpan(1, 10), new LineSpan(20, 30)];
    Assert.Equal(expected, c.Elided);
  }

  // ---- version invalidation ----

  [Fact]
  public void Check_VersionChanged_OldCoverageDrops()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 100);
    FileCoverage c = ledger.Check("f.txt", V2, 1, 100);
    Assert.Empty(c.Elided);
  }

  [Fact]
  public void Record_VersionChanged_ReplacesEntry()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 50);
    ledger.Record("f.txt", V2, 60, 100);
    // old version's coverage is gone; only 60-100 is held at V2
    FileCoverage c = ledger.Check("f.txt", V2, 1, 100);
    Assert.Equal([new LineSpan(60, 100)], c.Elided);
  }

  // ---- paths are independent ----

  [Fact]
  public void Check_DifferentPath_NotCovered()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("a.txt", V1, 1, 100);
    FileCoverage c = ledger.Check("b.txt", V1, 1, 100);
    Assert.Empty(c.Elided);
  }

  // ---- reset ----

  [Fact]
  public void Reset_ClearsAllCoverage()
  {
    ReadFreshnessLedger ledger = new();
    ledger.Record("f.txt", V1, 1, 100);
    ledger.Reset();
    FileCoverage c = ledger.Check("f.txt", V1, 1, 100);
    Assert.Empty(c.Elided);
  }

  // ---- concurrency: parallel Check/Record of one path never throw ----

  [Fact]
  public async Task ConcurrentCheckAndRecord_NeverThrows()
  {
    ReadFreshnessLedger ledger = new();
    Task[] tasks = [.. Enumerable.Range(0, 8).Select(i => Task.Run(() =>
    {
      for (int n = 0; n < 200; n++)
      {
        string path = "p" + (i % 2);
        ledger.Record(path, V1, 1, 100 + i);
        _ = ledger.Check(path, V1, 1, 50);
      }
    }))];
    await Task.WhenAll(tasks);
  }

  // ---- FileCoverage helpers ----

  [Fact]
  public void FileCoverage_IsEmpty_TrueWhenNoSpans()
  {
    Assert.True(new FileCoverage([]).IsEmpty);
    Assert.False(new FileCoverage([new LineSpan(1, 2)]).IsEmpty);
  }

  [Fact]
  public void Span_ToString_RendersRange()
      => Assert.Equal("10-50", new LineSpan(10, 50).ToString());
}

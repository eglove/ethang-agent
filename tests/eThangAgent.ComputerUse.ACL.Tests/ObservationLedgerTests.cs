namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Observation ledger contract (the batch brief, task 14): per-window
///     monotonic state ids, model-visible diff baselines that only tree-shown
///     observations update and that screenshot-only observations reset, and index
///     validation that distinguishes ELEMENT_UNAVAILABLE from STALE_STATE.</summary>
public class ObservationLedgerTests
{
  private static readonly LedgerKey Window = new(1234, 77);

  private static readonly LedgerWindow MainWin = new("Untitled - Notepad", 10, 20, 800, 600);

  private static CaptureAppElement El(int index, string role, string? title = null) =>
      new(index, role, "control", title, null, [0, 0, 10, 10], true, false, [], false, false, false, false, null, null, null);

  [Fact]
  public void StateIds_AreMonotonicPerWindow_NotGlobal()
  {
    ObservationLedger ledger = new();
    LedgerKey other = new(1234, 78);

    string first = ledger.Record(Window, MainWin, [El(0, "window", "Doc")], treeShownToModel: true, screenshotOnly: false);
    string otherFirst = ledger.Record(other, new LedgerWindow("Other", 0, 0, 100, 100), [El(0, "window", "Other")], treeShownToModel: true, screenshotOnly: false);
    string second = ledger.Record(Window, MainWin, [El(0, "window", "Doc")], treeShownToModel: true, screenshotOnly: false);

    Assert.Equal("s-1", first);
    Assert.Equal("s-1", otherFirst);
    Assert.Equal("s-2", second);
  }

  [Fact]
  public void ValidateIndex_FailsElementUnavailable_AfterRenumber()
  {
    ObservationLedger ledger = new();
    _ = ledger.Record(Window, MainWin, [El(0, "window", "Doc"), El(1, "button", "OK"), El(2, "edit", "Name")],
        treeShownToModel: true, screenshotOnly: false);

    Assert.True(ledger.ValidateIndex(Window, 2).Ok);

    _ = ledger.Record(Window, MainWin, [El(0, "window", "Doc"), El(1, "button", "OK")],
        treeShownToModel: true, screenshotOnly: false);

    LedgerIndexCheck renumbered = ledger.ValidateIndex(Window, 2);
    Assert.False(renumbered.Ok);
    Assert.Equal(ToolDomain.ComputerErrorCodes.ElementUnavailable, renumbered.Error);
    Assert.True(ledger.ValidateIndex(Window, 1).Ok);
  }

  [Fact]
  public void ValidateIndex_FailsStaleState_WhenWindowWasReplacedUnseen()
  {
    ObservationLedger ledger = new();
    _ = ledger.Record(Window, MainWin, [El(0, "window", "Doc"), El(1, "button", "OK")],
        treeShownToModel: true, screenshotOnly: false);

    // A capture the model never saw (a post-action receipt tree) replaced the window.
    LedgerWindow replaced = new("Save As", 40, 50, 500, 300);
    _ = ledger.Record(Window, replaced, [El(0, "window", "Save As"), El(1, "button", "OK")],
        treeShownToModel: false, screenshotOnly: false);

    LedgerIndexCheck stale = ledger.ValidateIndex(Window, 1);
    Assert.False(stale.Ok);
    Assert.Equal(ToolDomain.ComputerErrorCodes.StaleState, stale.Error);

    // Once the model SEES the new window, its indices are current again.
    _ = ledger.Record(Window, replaced, [El(0, "window", "Save As"), El(1, "button", "OK")],
        treeShownToModel: true, screenshotOnly: false);
    Assert.True(ledger.ValidateIndex(Window, 1).Ok);
  }

  [Fact]
  public void DiffBaseline_UpdatesOnlyOnModelVisibleTrees_AndResetsOnScreenshotOnly()
  {
    ObservationLedger ledger = new();
    _ = ledger.Record(Window, MainWin, [El(0, "window", "Doc"), El(1, "button", "OK")],
        treeShownToModel: true, screenshotOnly: false);

    LedgerDiffBase? baseline = ledger.TryGetDiffBase(Window);
    Assert.NotNull(baseline);
    Assert.Equal("s-1", baseline.StateId);
    Assert.Equal([new LedgerElementRef("window", "Doc"), new LedgerElementRef("button", "OK")],
        baseline.Elements);

    // A full observation the model never saw leaves the baseline alone.
    _ = ledger.Record(Window, MainWin, [El(0, "window", "Doc"), El(1, "button", "Save")],
        treeShownToModel: false, screenshotOnly: false);
    Assert.Equal("s-1", ledger.TryGetDiffBase(Window)!.StateId);

    // A screenshot-only observation resets the baseline.
    _ = ledger.Record(Window, MainWin, [], treeShownToModel: false, screenshotOnly: true);
    Assert.Null(ledger.TryGetDiffBase(Window));
  }

  [Fact]
  public void ValidateIndex_KeysRefsBySparseIndex_NotByListPosition()
  {
    ObservationLedger ledger = new();
    // A sparse table: indexes 0 and 5 only (delta/partial captures).
    _ = ledger.Record(Window, MainWin, [El(0, "window", "Doc"), El(5, "button", "OK")],
        treeShownToModel: true, screenshotOnly: false);

    // Index 5 exists even though the list has only two positions.
    Assert.True(ledger.ValidateIndex(Window, 5).Ok);
    // Position 1 is NOT an element - the sparse index space decides.
    LedgerIndexCheck gap = ledger.ValidateIndex(Window, 1);
    Assert.False(gap.Ok);
    Assert.Equal(ToolDomain.ComputerErrorCodes.ElementUnavailable, gap.Error);
  }

  [Fact]
  public void Record_DuplicateSparseIndexes_FailLoudly()
  {
    ObservationLedger ledger = new();
    _ = Assert.Throws<ArgumentException>(() => ledger.Record(Window, MainWin,
        [El(3, "button", "A"), El(3, "button", "B")], treeShownToModel: true, screenshotOnly: false));
  }
}

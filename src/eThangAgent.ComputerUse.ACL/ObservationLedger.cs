using System.Globalization;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>The observation ledger (spec 3.1/3.2): per app+window state ids that
///     only ever grow, the last MODEL-VISIBLE tree fingerprint per window (the
///     diff baseline - updated only when tree_shown_to_model was true, RESET by
///     screenshot-only observations), and index validation that separates a
///     renumbered tree (ELEMENT_UNAVAILABLE) from a replaced window the model
///     has not seen (STALE_STATE). Not thread-safe: one per access instance.
///     </summary>
public sealed class ObservationLedger
{
  private readonly Dictionary<LedgerKey, LedgerEntry> _entries = [];

  /// <summary>Records one capture against the window's ledger row. Returns the
  ///     newly minted, monotonically increasing state id for that window.</summary>
  public string Record(LedgerKey window, LedgerWindow fingerprint, IReadOnlyList<CaptureAppElement> elements,
      bool treeShownToModel, bool screenshotOnly)
  {
    ArgumentNullException.ThrowIfNull(window);
    ArgumentNullException.ThrowIfNull(fingerprint);
    ArgumentNullException.ThrowIfNull(elements);
    int next = 1;
    if (_entries.TryGetValue(window, out LedgerEntry? existing) && existing is not null)
    {
      next = existing.NextStateNumber + 1;
    }

    LedgerElementRef[] currentRefs = [.. elements.Select(e => new LedgerElementRef(e.Role, e.Title ?? ""))];

    // A screenshot-only observation RESETS the diff baseline: the model has no
    // tree to diff against until the next model-visible tree arrives.
    int? modelVisibleState = null;
    LedgerElementRef[]? modelVisibleRefs = null;
    LedgerWindow? baseline = null;
    if (!screenshotOnly && _entries.TryGetValue(window, out LedgerEntry? prior) && prior is not null)
    {
      modelVisibleState = prior.ModelVisibleState;
      modelVisibleRefs = prior.ModelVisibleRefs;
      baseline = prior.BaselineFingerprint;
    }

    if (treeShownToModel && !screenshotOnly)
    {
      modelVisibleState = next;
      modelVisibleRefs = currentRefs;
      baseline = fingerprint;
    }

    LedgerWindow? currentFingerprint = screenshotOnly ? null : fingerprint;
    _entries[window] = new LedgerEntry(next, currentRefs, currentFingerprint, baseline,
        modelVisibleState, modelVisibleRefs);
    return LedgerStateId(next);
  }

  /// <summary>Validates an element index against the model's visible state:
  ///     ok when the index exists in the CURRENT element set; ELEMENT_UNAVAILABLE
  ///     when the window is current but the index is absent (renumbered tree);
  ///     STALE_STATE when the window was replaced since the model last looked.</summary>
  public LedgerIndexCheck ValidateIndex(LedgerKey window, int index)
  {
    _ = _entries.TryGetValue(window, out LedgerEntry? found);
    return Validate(found, index);
  }

  private static LedgerIndexCheck Validate(LedgerEntry? entry, int index) => entry switch
  {
    null => new LedgerIndexCheck(false, ComputerErrorCodes.ElementUnavailable,
        "no observation of this window is on record; observe first."),
    _ when entry.ModelVisibleState is not int => new LedgerIndexCheck(false, ComputerErrorCodes.StaleState,
        "the model has not seen this window's current element tree; observe first."),
    _ when ReplacedSinceLastLook(entry) => new LedgerIndexCheck(false, ComputerErrorCodes.StaleState,
        $"the window changed since the model's last look (state s-{entry.ModelVisibleState}); observe first."),
    _ when index < 0 || index >= entry.CurrentRefs.Length => new LedgerIndexCheck(false,
        ComputerErrorCodes.ElementUnavailable,
        $"element index {index} does not exist in the latest observation of this window (0..{entry.CurrentRefs.Length - 1})."),
    _ => new LedgerIndexCheck(true, null, null),
  };

  /// <summary>True when the window's current fingerprint differs from the one
  ///     backing the model's last visible look.</summary>
  private static bool ReplacedSinceLastLook(LedgerEntry entry) => entry.BaselineFingerprint is LedgerWindow baseline
    && !baseline.Equals(entry.CurrentFingerprint);

  /// <summary>The diff baseline for the window: the state id and per-element
  ///     role+title pairs of the last tree the model saw; null after a reset
  ///     (screenshot-only observation) or before any model-visible tree.</summary>
  public LedgerDiffBase? TryGetDiffBase(LedgerKey window)
  {
    _ = _entries.TryGetValue(window, out LedgerEntry? found);
    return DiffBase(found);
  }

  private static LedgerDiffBase? DiffBase(LedgerEntry? entry) => entry switch
  {
    null => null,
    _ when entry.ModelVisibleState is int visible && entry.ModelVisibleRefs is LedgerElementRef[] refs
        => new LedgerDiffBase(LedgerStateId(visible), refs),
    _ => null,
  };

  /// <summary>The canonical s-&lt;n&gt; state id form.</summary>
  internal static string LedgerStateId(int number) => "s-" + number.ToString(CultureInfo.InvariantCulture);

  private sealed record LedgerEntry(
      int NextStateNumber,
      LedgerElementRef[] CurrentRefs,
      LedgerWindow? CurrentFingerprint,
      LedgerWindow? BaselineFingerprint,
      int? ModelVisibleState,
      LedgerElementRef[]? ModelVisibleRefs);
}

/// <summary>App+window ledger key: pid plus the broker's window id.</summary>
public sealed record LedgerKey(int Pid, int WindowId);

/// <summary>Window fingerprint for staleness: title and global rect. Two
///     captures of the same window id with different fingerprints mean the
///     window was replaced.</summary>
public sealed record LedgerWindow(string Title, int X, int Y, int Width, int Height);

/// <summary>One element identity row of a tree fingerprint: role plus title.
///     </summary>
public sealed record LedgerElementRef(string Role, string Title);

/// <summary>Result of ValidateIndex: ok, or the surface code naming the failure
///     (ELEMENT_UNAVAILABLE or STALE_STATE) plus a human message.</summary>
public sealed record LedgerIndexCheck(bool Ok, string? Error, string? Message);

/// <summary>The model-visible tree fingerprint: state id and role+title pairs.
///     </summary>
public sealed record LedgerDiffBase(string StateId, IReadOnlyList<LedgerElementRef> Elements);

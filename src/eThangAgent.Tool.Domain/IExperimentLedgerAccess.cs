using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One ledger a workspace holds, as a READER sees it (issue #318): the key it lives at,
///     its slug, and the counts that make the ledger list answer the question it opens on.
///     <para><see cref="Slug"/> is derived from the key rather than stored beside it: the key
///     <c>sci.&lt;slug&gt;/ledger</c> already IS the slug, and two holders of one fact can
///     disagree.</para></summary>
/// <param name="Key">The state key, e.g. <c>sci.my-plan/ledger</c>.</param>
/// <param name="Slug">The ledger's slug, e.g. <c>my-plan</c>.</param>
/// <param name="RecordCount">How many records it holds.</param>
/// <param name="OpenPredictions">Predictions carrying neither a verdict nor a skip - the number
///     D2 puts in the list, and the same one the finish gate reads.</param>
public sealed record LedgerSummary(string Key, string Slug, int RecordCount, int OpenPredictions)
{
  /// <summary>True when the loop is mid-flight: at least one prediction is waiting on its
  ///     verdict. A view sorts on this, because an open prediction is the fact a user scanning
  ///     the list is looking for.</summary>
  public bool HasOpenPredictions => OpenPredictions > 0;
}

/// <summary>The experiment ledger as a READER sees it (issue #318).
///     <para>READ-ONLY BY CONSTRUCTION (D1): there is no write method here, so a view built over
///     this port structurally cannot mutate a ledger. The writer is the experiment tool, through
///     <see cref="IExperimentLedgerStore"/>; a second writer would be a second source of truth,
///     which is why the two ports are deliberately separate rather than one interface with an
///     unused write method.</para>
///     <para>WHY THIS IS NOT <c>IStateService</c>: the view lives in the Desktop and would
///     otherwise hold a writer it must never use. A read-only port enforces D1 with the type
///     system rather than with discipline.</para></summary>
public interface IExperimentLedgerAccess
{
  /// <summary>Every ledger in this workspace, ordered by slug.
  ///     <para>IT PARSES EVERY LEDGER'S VALUE, because the counts D2 needs ARE facts about the
  ///     records and there is no cheaper honest source. It deliberately does NOT retain the
  ///     lines: the detail read fetches one ledger, so a list of forty does not hold a megabyte
  ///     of records nobody is looking at.</para></summary>
  /// <param name="ct">Cancellation.</param>
  Task<Result<IReadOnlyList<LedgerSummary>>> ListAsync(CancellationToken ct = default);

  /// <summary>One ledger's records, in stored order. A key that does not exist yet reads as
  ///     <see cref="ExperimentLedgerSnapshot.Empty"/> - an absent ledger and an empty one are
  ///     the same fact to a reader.</summary>
  /// <param name="key">The ledger's state key.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ExperimentLedgerSnapshot>> ReadAsync(string key, CancellationToken ct = default);
}

/// <summary>The shape of an experiment ledger's state key (issue #318), stated ONCE so the
///     writer, the reader, and the view cannot spell it three ways.
///     <para>The key is <c>sci.&lt;slug&gt;/ledger</c>: the <c>sci.</c> prefix and the
///     <c>ledger</c> name are what the experiment tool writes and what
///     <see cref="ExperimentLedgerReader"/> reads.</para></summary>
public static class ExperimentLedgerKey
{
  /// <summary>The namespace prefix every experiment ledger lives under.</summary>
  public const string NamespacePrefix = "sci.";

  /// <summary>The key name every experiment ledger uses.</summary>
  public const string Name = "ledger";

  /// <summary>True when a state key is an experiment ledger.
  ///     <para>THE NAMESPACE TEST IS A PREFIX, not an equality, because a slug may contain dots,
  ///     so the namespace is <c>sci.&lt;slug&gt;</c>. No case folding and no whitespace trimming:
  ///     state keys are exact, and a fuzzy match would pick up a key the tool would never read.
  ///     </para></summary>
  /// <param name="key">The state key to test.</param>
  public static bool IsLedger(string key)
  {
    ArgumentNullException.ThrowIfNull(key);
    int slash = key.IndexOf('/', StringComparison.Ordinal);
    return slash > NamespacePrefix.Length
        && key.StartsWith(NamespacePrefix, StringComparison.Ordinal)
        && string.Equals(key[(slash + 1)..], Name, StringComparison.Ordinal);
  }

  /// <summary>The slug a ledger key carries: the namespace with its <c>sci.</c> prefix removed.
  ///     A key that is not a ledger key yields its namespace unchanged, so a caller can never be
  ///     handed a silently truncated string.</summary>
  /// <param name="key">The ledger's state key.</param>
  public static string SlugOf(string key)
  {
    ArgumentNullException.ThrowIfNull(key);
    int slash = key.IndexOf('/', StringComparison.Ordinal);
    string ns = slash > 0 ? key[..slash] : key;
    return ns.StartsWith(NamespacePrefix, StringComparison.Ordinal)
        ? ns[NamespacePrefix.Length..]
        : ns;
  }
}

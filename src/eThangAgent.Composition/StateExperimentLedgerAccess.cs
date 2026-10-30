using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: exposes the workspace's durable state rows to the workbench's
///     ledger view through the Tool Domain's read-only IExperimentLedgerAccess port (issue #318).
///     <para>THE SAME TRANSLATION SITE AS <see cref="StateServiceExperimentLedgerStore"/>: the
///     Tool Domain cannot reference the State Domain, and a domain should not depend on another
///     context's contract anyway, so the one place that knows both lives with the shared wiring.
///     </para>
///     <para>READ-ONLY, AND THAT IS THE WHOLE POINT (D1): this class exposes the two reads and
///     nothing else, so a rail view holding it cannot write a ledger even by accident. The
///     write side stays on <see cref="IExperimentLedgerStore"/>, where the experiment tool owns
///     it.</para>
///     <para>IT READS THE STORE, NOT <c>IStateService</c>, for two reasons. The service's
///     <c>ListAsync</c> returns DISPLAY strings (<c>'ns/name v3'</c>) rather than keys, so a
///     reader that filtered them would have to parse a presentation format - and would silently
///     match nothing, which is exactly what it did before this was caught. Reading the store
///     also answers the whole list in ONE query instead of one round trip per key.</para>
///     <para>THE LIST PARSES EACH LEDGER'S VALUE, because the counts D2 needs ARE facts about
///     the records and there is no cheaper honest source. It deliberately does NOT retain the
///     lines: the detail read fetches one ledger, so a list of forty does not hold a megabyte of
///     records nobody is looking at.</para></summary>
internal sealed class StateExperimentLedgerAccess(IStateStore store, IWorkspaceContext workspace)
    : IExperimentLedgerAccess
{
  private readonly IStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IWorkspaceContext _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

  public async Task<Result<IReadOnlyList<LedgerSummary>>> ListAsync(CancellationToken ct = default)
  {
    // The namespace filter is the store's own, so a workspace with hundreds of keys does not
    // ship them all across the wire to be filtered here.
    IReadOnlyList<StateKeyValue> rows = await _store
        .ListKeysAsync(_workspace.WorkspaceId, null, ct).ConfigureAwait(false);

    List<LedgerSummary> ledgers = [];
    foreach (StateKeyValue row in rows
        .Where(row => ExperimentLedgerKey.IsLedger(KeyOf(row)))
        .OrderBy(row => ExperimentLedgerKey.SlugOf(KeyOf(row)), StringComparer.Ordinal))
    {
      ExperimentLedgerSnapshot snapshot = ExperimentLedgerReader.Read(row.Value);
      ledgers.Add(new LedgerSummary(KeyOf(row), ExperimentLedgerKey.SlugOf(KeyOf(row)),
          snapshot.RecordCount, snapshot.OpenPredictions));
    }

    return Result.Success<IReadOnlyList<LedgerSummary>>(ledgers);
  }

  public async Task<Result<ExperimentLedgerSnapshot>> ReadAsync(string key, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(key);
    Result<(string Ns, string Name)> parsed = StateKey.Parse(key);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<ExperimentLedgerSnapshot>(parsed.Error);
    }

    StateKeyValue? row = await _store
        .GetKeyAsync(_workspace.WorkspaceId, parsed.Value.Ns, parsed.Value.Name, ct)
        .ConfigureAwait(false);

    // A ledger nobody has written yet is EMPTY, not missing: the experiment tool creates the key
    // on the first append, so 'not found' is the ordinary state of a plan whose loop has not
    // started - and rendering that as a load failure would make an untouched workspace look
    // broken.
    return Result.Success(row is null
        ? ExperimentLedgerSnapshot.Empty
        : ExperimentLedgerReader.Read(row.Value));
  }

  /// <summary>The namespaced key a row lives at. The store returns the two segments separately
  ///     because they are its columns; the ledger vocabulary speaks one key.</summary>
  /// <param name="row">The state row.</param>
  private static string KeyOf(StateKeyValue row) => $"{row.Ns}/{row.Name}";
}

using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for the append-only experiment ledger behind the
///     experiment tool. The composition root adapts the State Domain's IStateService to
///     it — the domain depends only on this contract, never on another context's types,
///     so any store that can express read + atomic single-line append can be wired in
///     without domain changes. The ledger is append-only by contract: there is no
///     overwrite and no delete.</summary>
public interface IExperimentLedgerStore
{
  /// <summary>Reads the raw value stored under <paramref name="key"/> (one record per
  ///     line), or fails with KeyNotFound when the key does not exist yet.</summary>
  Task<Result<string>> ReadAsync(string key, CancellationToken ct = default);

  /// <summary>Atomic CAS append of one line. A missing key is created with the single
  ///     line; when <paramref name="expectedVersion"/> is supplied it must match the
  ///     stored version (VersionConflict otherwise). <paramref name="text"/> must be a
  ///     single line without leading or trailing whitespace — the adapter's underlying
  ///     store enforces this and fails closed with InvalidText. Returns the new version.</summary>
  Task<Result<int>> AppendAsync(string key, string text, int? expectedVersion,
      CancellationToken ct = default);
}

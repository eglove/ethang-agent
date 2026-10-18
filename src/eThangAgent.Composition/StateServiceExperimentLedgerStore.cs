using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: exposes the State Domain's IStateService to the
///     experiment tool through the Tool Domain's own IExperimentLedgerStore port.
///     Tool.Domain cannot reference State.Domain directly (State.Domain →
///     Capability.Domain → Tool.Domain), and a domain should not depend on another
///     context's contract anyway; this translation lives with the shared wiring.
///     IStateService.AppendAsync already provides atomic CAS single-line append with
///     the missing-key-creates-key contract the ledger needs, so the adapter is a
///     pure translation and adds no storage of its own.</summary>
internal sealed class StateServiceExperimentLedgerStore(IStateService state) : IExperimentLedgerStore
{
  public Task<Result<string>> ReadAsync(string key, CancellationToken ct = default) =>
      state.GetAsync(key, ct);

  public async Task<Result<int>> AppendAsync(string key, string text,
      int? expectedVersion, CancellationToken ct = default) =>
      (await state.AppendAsync(key, text, expectedVersion, ct).ConfigureAwait(false))
          .Map(kv => kv.Version);
}

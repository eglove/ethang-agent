using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: exposes the State Domain's IStateService to the experiment
///     tool through the Tool Domain's own <see cref="IRefutationStore"/> port (issue #315).
///     <para>THE SAME REASONING AS <see cref="StateServiceExperimentLedgerStore"/>: Tool.Domain
///     cannot reference State.Domain (State.Domain → Capability.Domain → Tool.Domain), and a
///     domain should not depend on another context's contract anyway. This translation lives
///     with the shared wiring.</para>
///     <para>WHY THIS IS A <c>SetAsync</c> AND NOT AN APPEND: a refutation is idempotent by
///     construction - one prediction takes one verdict, so the rendered value for a given
///     (slug, prediction id) never changes - and an append would duplicate a record whose whole
///     purpose is to be found once. The state store's CAS write with a null expectedVersion
///     replaces the row, which is exactly the semantics the port names.</para>
///     <para>WORKSPACE SCOPE IS THE STATE SERVICE'S: it is bound to one workspace's id, so a
///     refutation written here is visible to every chain in that workspace and to nothing
///     outside it.</para></summary>
internal sealed class StateServiceRefutationStore(IStateService state) : IRefutationStore
{
  public async Task<Result<int>> WriteAsync(string key, string value,
      CancellationToken ct = default) =>
      (await state.SetAsync(key, value, null, ct).ConfigureAwait(false))
          .Map(kv => kv.Version);
}
